#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenRA.Traits;
using RLProto = OpenRA.Mods.Common.RL;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("External bot bridge for reinforcement learning agents. " +
		"Hosts a gRPC server that streams observations and receives actions.")]
	[TraitLocation(SystemActors.Player)]
	public sealed class ExternalBotBridgeInfo : TraitInfo, IBotInfo
	{
		[FieldLoader.Require]
		[Desc("Internal id for this bot.")]
		public readonly string Type = null;

		[FluentReference]
		[Desc("Human-readable name this bot uses.")]
		public readonly string Name = null;

		[Desc("gRPC port for the RL agent to connect to.")]
		public readonly int Port = 9999;

		[Desc("How many game ticks between observations sent to the agent. " +
			"1 = every tick, 10 = every 10th tick.")]
		public readonly int ObservationInterval = 1;

		string IBotInfo.Type => Type;
		string IBotInfo.Name => Name;

		public override object Create(ActorInitializer init) { return new ExternalBotBridge(this, init); }
	}

	public sealed class ExternalBotBridge : ITick, IBot, INotifyCreated
	{
		/// <summary>
		/// Thread-safe session registry. In multi-session mode, multiple bridges
		/// coexist in one process, each identified by its session/episode ID.
		/// In single-session (legacy) mode, there's exactly one entry.
		/// </summary>
		internal static readonly ConcurrentDictionary<string, ExternalBotBridge> Sessions = new();

		/// <summary>
		/// True when running in multi-session mode (RLSessionManager manages lifecycle).
		/// When false, the bridge manages its own gRPC server and calls Game.Exit() on teardown.
		/// </summary>
		internal static volatile bool MultiSessionMode;

		/// <summary>
		/// In multi-session mode, set this before World creation to assign
		/// a specific session ID to the next bridge that gets constructed.
		/// ThreadStatic so multiple init threads can create Worlds concurrently.
		/// </summary>
		[ThreadStatic]
		internal static string NextSessionId;

		static WebApplication grpcApp;
		static bool grpcServerStarted;
		static readonly ManualResetEventSlim GrpcListen = new(false);
		static volatile bool grpcListenOk;

		/// <summary>
		/// GUI skirmish must not share docker compose's host :9999 (train).
		/// Headless / MultiSession keep yaml Port (9999).
		/// </summary>
		const int GuiGrpcPort = 10001;
		static readonly object GrpcLock = new();

		public volatile bool IsEnabled;

		readonly ExternalBotBridgeInfo info;
		readonly World world;
		readonly Queue<Order> orders = new();
		readonly string episodeId;

		Player player;
		ObservationSerializer observationSerializer;
		ActionHandler actionHandler;

		// Channels for async communication between game thread and gRPC stream.
		// DropOldest ensures the game thread never blocks:
		//   - Observations: capacity=1, latest overwrites stale (agent always gets freshest state)
		//   - Actions: capacity=16, game drains all pending each tick
		readonly Channel<RLProto.GameObservation> observationChannel =
			Channel.CreateBounded<RLProto.GameObservation>(
				new BoundedChannelOptions(1)
				{
					FullMode = BoundedChannelFullMode.DropOldest,
					SingleWriter = true,
					SingleReader = true,
				});

		readonly Channel<RLProto.AgentAction> actionChannel =
			Channel.CreateBounded<RLProto.AgentAction>(
				new BoundedChannelOptions(16)
				{
					FullMode = BoundedChannelFullMode.DropOldest,
					SingleWriter = true,
					SingleReader = true,
				});

		volatile bool agentConnected;
		bool connectionLostHandled;

		// Fast-forward: when > 0, game runs at max speed until this tick is reached.
		// Volatile: written by gRPC thread in RequestFastAdvance, read by game thread in Tick.
		volatile int pendingFastAdvanceTarget;

		// Unary FastAdvance: TaskCompletionSource completed when target tick is reached.
		// Must be set BEFORE pendingFastAdvanceTarget (volatile write provides release fence).
		volatile TaskCompletionSource<RLProto.GameObservation> pendingAdvanceResult;

		// FastAdvance(ticks <= 0): inject commands on the game thread and return
		// the current observation without changing tickScale. Used for GUI skirmish
		// vs a human (the world already ticks at 25 tps).
		volatile bool pendingRealtimeSnapshot;

		Process ppoSidecar;

		// Server-side interrupt detection state
		volatile int interruptCheckInterval;  // check every N ticks (0 = disabled)
		int interruptNextCheckTick;           // next tick to run interrupt checks
		int fastAdvanceStartTick;             // tick when current advance started
		HashSet<string> enabledInterrupts = new();

		// Previous-state tracking for delta-based interrupt detection
		readonly HashSet<uint> prevVisibleEnemyIds = new();
		readonly HashSet<uint> prevOwnUnitIds = new();
		readonly Dictionary<uint, float> prevUnitHpPct = new();
		readonly HashSet<uint> prevEnemyBuildingIds = new();
		readonly HashSet<uint> prevOwnBuildingIds = new();
		float prevExploredPct;

		// Reusable collections for CheckInterrupts() (avoid GC pressure)
		readonly HashSet<uint> curEnemyIds = new();
		readonly HashSet<uint> curOwnUnitIds = new();
		readonly Dictionary<uint, float> curOwnUnitHp = new();
		readonly HashSet<uint> curEnemyBuildingIds = new();
		readonly HashSet<uint> curOwnBuildingIds = new();

		// Track which own units were moving (not idle) for unit_arrived detection
		readonly HashSet<uint> prevMovingUnitIds = new();

		// Cached actor references: populated once at advance start via SnapshotActors(),
		// then reused for fast delta checks. Full refresh every 4th check to catch
		// newly spawned actors (harvesters, produced units). This avoids expensive
		// ActorsHavingTrait<T>() enumeration on every check across 64 sessions.
		readonly List<Actor> cachedMobileActors = new();
		readonly List<Actor> cachedBuildingActors = new();
		int interruptCheckCount;
		const int FullRefreshEveryNChecks = 4;

		string pendingInterruptReason;

			// ── Capa 0: declaración temprana de victoria (win_early) ──────────
			// Si n_buildings_production_enemy==0 o patrimonio_enemy <10% propio
			// durante 500 ticks → win_early. Diferenciable de win del motor
			// (result="win_early" vs "win") pero mismo w_win para PPO.
			int earlyWinStreak;
			const int EarlyWinThreshold = 500;
			bool earlyWinDeclared;
			internal static readonly ConcurrentDictionary<string, string> EarlyWinReasonBySession = new();

			bool IsEarlyWinConditionMet(out string reason)
			{
				reason = null;
				if (world == null || player == null)
					return false;
				if (world.WorldTick < 2000)
					return false;
				// Enemigos: TODOS los players enemigos (no solo el primero) — evita
				// falso positivo si se cuela neutral/observer con 0 edificios.
				var enemyPlayers = world.Players.Where(p => p != player && !p.NonCombatant).ToArray();
				if (enemyPlayers.Length == 0)
					return false;
				// Conteo espectador exacto (sin niebla) — igual que SerializeGlobalSummary
				// pero agregado sobre todos los enemigos (RA: 1v1, pero robusto si hay más).
				int ownN = 0, eneN = 0;
				int ownProd = 0, eneProd = 0;
				int ownCash = 0, eneCash = 0;
				int ownUnitVal = 0, eneUnitVal = 0;
				int ownBldVal = 0, eneBldVal = 0;
				// Set de ids enemigos para clasificación rápida de actores
				var enemySet = new HashSet<Player>(enemyPlayers);
				foreach (var a in world.Actors)
				{
					if (a.IsDead || !a.IsInWorld || a == world.WorldActor)
						continue;
					var owner = a.Owner;
					if (owner == null || owner.NonCombatant)
						continue;
					// Clasificación robusta: own si owner==player, enemy si owner en enemyPlayers
					var isOwn = owner == player;
					var isEnemy = enemySet.Contains(owner);
					if (!isOwn && !isEnemy)
						continue;
					var valued = a.Info.TraitInfoOrDefault<ValuedInfo>();
					if (valued == null)
						continue;
					bool isBuilding = a.Info.HasTraitInfo<BuildingInfo>();
					if (isBuilding)
					{
						if (isOwn) { ownBldVal += valued.Cost; ownN++; }
						else { eneBldVal += valued.Cost; eneN++; }
						// Producción: robusto — cuenta si el edificio TIENE alguna queue,
						// aunque esté pausada/desactivada por power. Usa TraitsImplementing
						// (no TraitOrDefault que a veces da null por inicialización).
						var hasQueue = a.TraitsImplementing<ProductionQueue>().Any();
						if (hasQueue)
						{
							if (isOwn) ownProd++;
							else eneProd++;
						}
					}
					else
					{
						if (isOwn) ownUnitVal += valued.Cost;
						else eneUnitVal += valued.Cost;
					}
				}
				var ownRes = player.PlayerActor.TraitOrDefault<PlayerResources>();
				if (ownRes != null) ownCash = ownRes.Cash;
				// Cash enemigo: suma de todos los enemigos (evita elegir solo uno con 3k inicial)
				foreach (var ep in enemyPlayers)
				{
					var r = ep.PlayerActor.TraitOrDefault<PlayerResources>();
					if (r != null) eneCash += r.Cash;
				}
				int ownTotal = ownCash + ownUnitVal + ownBldVal;
				int eneTotal = eneCash + eneUnitVal + eneBldVal;
				// Debug periódico (cada 1000 ticks) para cazar futuros falsos positivos sin rebuildear
				if (world.WorldTick % 1000 == 0)
				{
					var ownBldNames = string.Join(",", world.Actors.Where(a => !a.IsDead && a.IsInWorld && a.Owner == player && a.Info.HasTraitInfo<BuildingInfo>()).Take(8).Select(a => a.Info.Name));
					Log.Write("rl-bridge", $"early_check t={world.WorldTick} ownN={ownN} prod={ownProd} tot={ownTotal} | eneN={eneN} prod={eneProd} tot={eneTotal} cashO={ownCash} cashE={eneCash} bldOwn=[{ownBldNames}]");
				}
				// Guardas: no declarar si aún no tenemos economía NI el enemigo tiene base mínima
				if (ownN < 3 || ownTotal < 2000)
					return false;
				if (ownProd == 0)
					return false; // no declarar si ni siquiera nosotros producimos (falso positivo simétrico)
				// Requiere que el enemigo haya tenido base alguna vez — si eneN==0 pero
				// el enemigo nunca spawneó (mapa mal) no declarar; en cambio si tenía
				// edificios y ahora 0 sí es victoria real.
				if (eneN == 0)
				{
					// Solo gana si además patrimonio colapsó (evita no_prod_0bld espurio)
					if (eneTotal * 10 < ownTotal && eneTotal < 1000)
					{
						reason = $"raze_0bld_{eneTotal}vs{ownTotal}";
						return true;
					}
					return false;
				}
				// Condición 1: enemigo sin producción viva — DESACTIVADA (falso positivo: prod==0 para ambos lados en 2000)
				// El conteo TraitsImplementing<ProductionQueue> daba 0 incluso con ConYard vivo.
				// Hasta tener detection robusta (Production trait), solo gana por raze o patrimonio.
				//if (eneProd == 0)
				//{
				//	reason = $"no_prod_{eneN}bld";
				//	return true;
				//}
				// Condición 2: patrimonio <10% del propio
				if (eneTotal > 0 && ownTotal > 0 && eneTotal * 10 < ownTotal)
				{
					reason = $"patrimonio_{eneTotal}vs{ownTotal}";
					return true;
				}
				return false;
			}

			/// <summary>
		/// Signaled when the session is done (game over or destroyed). Used by
		/// RLSessionManager to know when to stop the tick loop for this session.
		/// </summary>
		internal readonly ManualResetEventSlim SessionDone = new(false);

		/// <summary>
		/// Signaled when the game thread should wake up and tick (e.g. FastAdvance queued).
		/// Auto-resets after each wait so the thread blocks again when idle.
		/// </summary>
		internal readonly AutoResetEvent TickRequested = new(false);

		IBotInfo IBot.Info => info;
		Player IBot.Player => player;

		/// <summary>Session/episode ID used as the routing key for gRPC calls.</summary>
		public string SessionId => episodeId;

		public ExternalBotBridge(ExternalBotBridgeInfo info, ActorInitializer init)
		{
			this.info = info;
			world = init.World;

			// In multi-session mode, use the pre-assigned session ID so the bridge
			// registers under the correct ID for gRPC routing from the start.
			if (MultiSessionMode && !string.IsNullOrEmpty(NextSessionId))
				episodeId = NextSessionId;
			else
				episodeId = Guid.NewGuid().ToString("N")[..12];
		}

		void INotifyCreated.Created(Actor self)
		{
			// gRPC server is started in Activate() (single-session) or by RLSessionManager (multi-session)
		}

		/// <summary>
		/// Start the gRPC server. Called from Activate() in single-session mode,
		/// or from RLSessionManager.StartGrpcServer() in multi-session mode.
		/// </summary>
		internal static void StartGrpcServer(int port)
		{
			lock (GrpcLock)
			{
				if (grpcServerStarted)
					return;

				grpcServerStarted = true;
			}

			try
			{
				var builder = WebApplication.CreateBuilder(new WebApplicationOptions
				{
					Args = []
				});

				builder.WebHost.ConfigureKestrel(options =>
				{
					options.ListenAnyIP(port, listenOptions =>
						listenOptions.Protocols = HttpProtocols.Http2);
				});

				builder.Services.AddGrpc();

				// Suppress all ASP.NET Core console logging
				builder.Logging.ClearProviders();

				grpcApp = builder.Build();
				grpcApp.MapGrpcService<RLBridgeService>();

				Log.Write("rl-bridge", $"gRPC server starting on port {port}");
				grpcApp.Lifetime.ApplicationStarted.Register(() =>
				{
					grpcListenOk = true;
					GrpcListen.Set();
				});
				grpcApp.Run();
			}
			catch (Exception e)
			{
				Log.Write("rl-bridge", $"gRPC server failed: {e}");
				lock (GrpcLock)
				{
					grpcServerStarted = false;
				}

				grpcListenOk = false;
				GrpcListen.Set();
			}
		}

		public void Activate(Player p)
		{
			if (p.World.IsReplay)
				return;

			IsEnabled = true;
			player = p;
			observationSerializer = new ObservationSerializer(world, player, episodeId);
			actionHandler = new ActionHandler(world, player);

			// Register in the session registry
			Sessions[episodeId] = this;

			// Pause game until RL agent connects (gives LLM time to plan)
			if (MultiSessionMode)
			{
				// In multi-session mode, use SetLocalPauseState to avoid queuing
				// a PauseGame Order that would re-pause during the first tick.
				world.SetLocalPauseState(true);
			}
			else
			{
				// In single-session mode, use SetPauseState so the pause order
				// is recorded in the replay for correct playback.
				world.SetPauseState(true);
			}

			Log.Write("rl-bridge", "Game paused — waiting for RL agent to connect");

			Log.Write("rl-bridge", $"ExternalBotBridge activated for player {p.InternalName}, session {episodeId}");

			// In single-session (legacy) mode, start gRPC server from here
			if (!MultiSessionMode)
			{
				var port = info.Port;
				var envPort = Environment.GetEnvironmentVariable("RL_GRPC_PORT");
				if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out var pp))
					port = pp;
				else if (!Game.IsHeadless)
					port = GuiGrpcPort;

				var alreadyListening = grpcServerStarted && grpcListenOk;
				if (!Game.IsHeadless && !alreadyListening)
				{
					GrpcListen.Reset();
					grpcListenOk = false;
				}

				var thread = new Thread(() => StartGrpcServer(port))
				{
					IsBackground = true,
					Name = "RL-Bridge-gRPC"
				};
				thread.Start();

				// GUI skirmish: spawn the Python PPO sidecar so picking
				// "PPO Agent" in the lobby is enough. Headless train does not.
				if (!Game.IsHeadless)
				{
					var listenOk = alreadyListening
						|| (GrpcListen.Wait(5000) && grpcListenOk);
					if (!listenOk)
					{
						Log.Write("rl-bridge",
							$"gRPC :{port} did not listen — not starting sidecar. " +
							"Is docker/train using this port? GUI default is 10001.");
					}
					else
						TryStartPpoSidecar(port);

					var captured = this;
					new Thread(() =>
					{
						Thread.Sleep(30000);
						if (!captured.agentConnected && captured.IsEnabled)
						{
							Log.Write("rl-bridge", "PPO sidecar did not connect in 30s — unpausing so the human is not stuck");
							try { world.SetLocalPauseState(false); }
							catch (Exception e) { Log.Write("rl-bridge", $"unpause timeout: {e.Message}"); }
						}
					})
					{
						IsBackground = true,
						Name = "RL-Bridge-UnpauseTimeout"
					}.Start();
				}
			}
		}

		/// <summary>
		/// Deactivate and clean up this session. In multi-session mode, this replaces
		/// Game.Exit() — only this session is torn down, not the entire process.
		/// </summary>
		internal void Deactivate()
		{
			Sessions.TryRemove(episodeId, out _);
			SessionDone.Set();
			TickRequested.Set(); // Wake tick loop so it can exit

			// Complete any pending FastAdvance so the gRPC call doesn't hang forever
			pendingAdvanceResult?.TrySetCanceled();
			pendingRealtimeSnapshot = false;
			TryStopPpoSidecar();

			Log.Write("rl-bridge", $"Session {episodeId} deactivated");
		}

		internal bool HasPendingAdvance => pendingAdvanceResult != null;

		/// <summary>
		/// TickSession / worker must never finish while FastAdvance is still
		/// waiting: a paused first TryTick or game-over-before-ITick used to
		/// leave the unary RPC hung until the Python 110s deadline.
		/// No-op if ITick already completed the TCS.
		/// </summary>
		internal void CompletePendingAdvance(string reason)
		{
			var tcs = pendingAdvanceResult;
			if (tcs == null)
				return;
			pendingAdvanceResult = null;
			pendingFastAdvanceTarget = 0;
			pendingRealtimeSnapshot = false;
			try { world.SetTickScale(1.0f); } catch { /* world may be disposing */ }
			try
			{
				var obs = observationSerializer.Serialize(world.WorldTick);
				if (world.IsGameOver)
				{
					obs.Done = true;
					obs.Result = player != null && player.WinState == WinState.Won ? "win" : "lose";
				}

				Log.Write("rl-bridge",
					$"CompletePendingAdvance ({reason}) tick={world.WorldTick} session={episodeId}");
				tcs.TrySetResult(obs);
			}
			catch (Exception e)
			{
				Log.Write("rl-bridge", $"CompletePendingAdvance failed ({reason}): {e.Message}");
				tcs.TrySetException(e);
			}
		}

		internal void FailPendingAdvance(string reason)
		{
			var tcs = pendingAdvanceResult;
			if (tcs == null)
				return;
			pendingAdvanceResult = null;
			pendingFastAdvanceTarget = 0;
			pendingRealtimeSnapshot = false;
			try { world.SetTickScale(1.0f); } catch { }
			Log.Write("rl-bridge", $"FailPendingAdvance ({reason}) session={episodeId}");
			tcs.TrySetException(new RpcException(new Status(StatusCode.Unavailable, reason)));
		}

		void IBot.QueueOrder(Order order)
		{
			orders.Enqueue(order);
		}

		bool gameOverHandled;

		void ITick.Tick(Actor self)
		{
			if (!IsEnabled || self.World.IsLoadingGameSave)
				return;

			// Detect game over in Tick (IGameOver.GameOver doesn't fire on Player traits)
			if (!gameOverHandled && world.IsGameOver)
			{
				gameOverHandled = true;
				Log.Write("rl-bridge", $"Game over detected at tick {world.WorldTick}, session {episodeId}");

				// Complete any pending FastAdvance so the gRPC call returns immediately
				// instead of hanging until the 300s deadline.
				var gameOverTcs = pendingAdvanceResult;
				if (gameOverTcs != null)
				{
					try
					{
						var obs = observationSerializer.Serialize(world.WorldTick);
						obs.Done = true;
						obs.Result = player.WinState == WinState.Won ? "win" : "lose";
						gameOverTcs.TrySetResult(obs);
					}
					catch (Exception e)
					{
						gameOverTcs.TrySetException(e);
					}

					pendingAdvanceResult = null;
					pendingFastAdvanceTarget = 0;
					world.SetTickScale(1.0f);
				}

				if (MultiSessionMode)
				{
					// In multi-session mode, just deactivate — don't exit the process
					Deactivate();
				}
				else
				{
					ScheduleProcessExit("game over", 3000);
				}
			}
			// ── Capa 0: chequeo win_early cada Tick (500 ticks sostenidos) ─
			// GUI skirmish vs a human: never steal the match with a patrimonio rule.
			if (!world.IsGameOver && !earlyWinDeclared && Game.IsHeadless)
			{
				if (IsEarlyWinConditionMet(out var earlyReason))
				{
					earlyWinStreak++;
					if (earlyWinStreak >= EarlyWinThreshold)
					{
						earlyWinDeclared = true;
						EarlyWinReasonBySession[episodeId] = earlyReason;
						Log.Write("rl-bridge", $"Early win declared for {episodeId}: {earlyReason} at tick {world.WorldTick} (streak {earlyWinStreak})");
						var enemyPlayer = world.Players.FirstOrDefault(pi => pi != player && !pi.NonCombatant && !pi.IsAlliedWith(player));
						player.WinState = WinState.Won;
						if (enemyPlayer != null)
							enemyPlayer.WinState = WinState.Lost;
						world.OnPlayerWinStateChanged(player);
						if (enemyPlayer != null)
							world.OnPlayerWinStateChanged(enemyPlayer);
						world.EndGame();
						var earlyTcs = pendingAdvanceResult;
						if (earlyTcs != null)
						{
							try
							{
								var obsEarly = observationSerializer.Serialize(world.WorldTick);
								obsEarly.Done = true;
								obsEarly.Result = "win_early";
								earlyTcs.TrySetResult(obsEarly);
							}
							catch (Exception e)
							{
								earlyTcs.TrySetException(e);
							}
							pendingAdvanceResult = null;
							pendingFastAdvanceTarget = 0;
							world.SetTickScale(1.0f);
						}
						gameOverHandled = true;
						if (MultiSessionMode)
							Deactivate();
						else
							ScheduleProcessExit("win_early", 3000);
						return;
					}
				}
				else
					earlyWinStreak = 0;
			}

			// Detect internal connection loss (TCP between client and embedded server)
			if (!connectionLostHandled && !world.IsConnectionAlive)
			{
				connectionLostHandled = true;
				Log.Write("rl-bridge", $"Internal connection lost at tick {world.WorldTick}! Session {episodeId}.");

				// Complete any pending FastAdvance so gRPC doesn't hang
				var connLostTcs = pendingAdvanceResult;
				if (connLostTcs != null)
				{
					connLostTcs.TrySetCanceled();
					pendingAdvanceResult = null;
					pendingFastAdvanceTarget = 0;
					world.SetTickScale(1.0f);
				}

				if (MultiSessionMode)
				{
					Deactivate();
				}
				else
				{
					ScheduleProcessExit("connection lost", 500);
				}

				return;
			}

			// Check for server-side interrupts during fast-forward
			if (pendingFastAdvanceTarget > 0 && interruptCheckInterval > 0
				&& world.WorldTick >= interruptNextCheckTick
				&& pendingInterruptReason == null)
			{
				interruptNextCheckTick = world.WorldTick + interruptCheckInterval;
				try
				{
					var signal = CheckInterrupts();
					if (signal != null)
					{
						pendingInterruptReason = signal;
						// Stop advancing — complete on the next Tick check below
						pendingFastAdvanceTarget = world.WorldTick;
						Console.Error.WriteLine($"[rl-bridge] Interrupt [{signal}] at tick {world.WorldTick} (started at {fastAdvanceStartTick})");
					}
				}
				catch (Exception e)
				{
					Console.Error.WriteLine($"[rl-bridge] CheckInterrupts ERROR at tick {world.WorldTick}: {e}");
					// Disable further checks to avoid repeated errors
					interruptCheckInterval = 0;
				}
			}

			// Check if fast-forward target tick has been reached (or interrupted)
			if (pendingFastAdvanceTarget > 0 && world.WorldTick >= pendingFastAdvanceTarget)
			{
				world.SetTickScale(1.0f);
				var reason = pendingInterruptReason;
				if (reason != null)
					Log.Write("rl-bridge", $"Fast-forward interrupted at tick {world.WorldTick} by [{reason}]");
				else
					Log.Write("rl-bridge", $"Fast-forward complete at tick {world.WorldTick}, restored normal speed");
				pendingFastAdvanceTarget = 0;

				// Complete unary FastAdvance if pending
				var advTcs = pendingAdvanceResult;
				if (advTcs != null)
				{
					try
					{
						var obs = observationSerializer.Serialize(world.WorldTick);
						// Set interrupt fields
						if (reason != null)
						{
							obs.Interrupted = true;
							obs.InterruptReason = reason;
						}
						obs.ActualTicksAdvanced = world.WorldTick - fastAdvanceStartTick;

						// Set explored_percent
						var shroud = player?.Shroud;
						if (shroud != null)
						{
							var totalCells = world.Map.AllCells.Count();
							var exploredCells = world.Map.AllCells.Count(c => shroud.IsExplored(c));
							obs.ExploredPercent = totalCells > 0 ? (float)exploredCells / totalCells * 100f : 0f;
						}

						advTcs.TrySetResult(obs);
					}
					catch (Exception e)
					{
						advTcs.TrySetException(e);
					}

					pendingAdvanceResult = null;
					pendingInterruptReason = null;
				}
			}

			try
			{
				// Process all pending actions from agent (non-blocking drain)
				while (actionChannel.Reader.TryRead(out var agentAction))
				{
					// Intercept FAST_ADVANCE before ActionHandler
					foreach (var cmd in agentAction.Commands)
					{
						if (cmd.Action == RLProto.ActionType.FastAdvance)
						{
							var ticks = Math.Max(1, Math.Min(cmd.Ticks, 5000));
							pendingFastAdvanceTarget = world.WorldTick + ticks;
							world.SetTickScale(0.025f);
							Log.Write("rl-bridge", $"Fast-forward: {ticks} ticks from {world.WorldTick} to {pendingFastAdvanceTarget} (tickScale=0.025)");
						}
					}

					actionHandler.ProcessAction(agentAction, this);
				}
			}
			catch (ChannelClosedException)
			{
				Log.Write("rl-bridge", "Agent disconnected (action channel closed)");
				agentConnected = false;
			}

			// Issue any queued orders
			while (orders.Count > 0)
				world.IssueOrder(orders.Dequeue());

			// Realtime snapshot (FastAdvance ticks<=0): serialize on this thread
			// after orders are issued, without changing tickScale.
			if (pendingRealtimeSnapshot)
			{
				pendingRealtimeSnapshot = false;
				var rtTcs = pendingAdvanceResult;
				pendingAdvanceResult = null;
				if (rtTcs != null)
				{
					try
					{
						var obs = observationSerializer.Serialize(world.WorldTick);
						if (world.IsGameOver)
						{
							obs.Done = true;
							obs.Result = player != null && player.WinState == WinState.Won ? "win" : "lose";
						}

						rtTcs.TrySetResult(obs);
					}
					catch (Exception e)
					{
						Log.Write("rl-bridge", $"Realtime snapshot failed: {e.Message}");
						rtTcs.TrySetException(e);
					}
				}
			}

			// Only send observations at the configured interval
			if (world.WorldTick % info.ObservationInterval != 0)
				return;

			if (!agentConnected)
				return;

			try
			{
				// Serialize and push observation (DropOldest — never blocks)
				var observation = observationSerializer.Serialize(world.WorldTick);
				observationChannel.Writer.TryWrite(observation);
			}
			catch (ChannelClosedException)
			{
				Log.Write("rl-bridge", "Agent disconnected (observation channel closed)");
				agentConnected = false;
			}
			catch (Exception e)
			{
				Log.Write("rl-bridge", $"Error serializing observation: {e}");
			}
		}

		/// <summary>
		/// Called by RLBridgeService to signal that an agent has connected.
		/// </summary>
		internal void OnAgentConnected()
		{
			agentConnected = true;

			// Use SetLocalPauseState for immediate effect — SetPauseState issues an Order
			// which requires the game loop to process it, but Tick() doesn't run while
			// Paused is true, creating a deadlock for custom maps where loading is slow.
			world.SetLocalPauseState(false);
			Log.Write("rl-bridge", "RL agent connected — game unpaused");
		}

		/// <summary>
		/// Called by RLBridgeService to signal that the agent has disconnected.
		/// In multi-session mode, deactivates the session. In legacy mode, exits the process.
		/// </summary>
		internal void OnAgentDisconnected()
		{
			agentConnected = false;

			if (MultiSessionMode)
			{
				Log.Write("rl-bridge", $"RL agent disconnected from session {episodeId}");
				Deactivate();
			}
			else
			{
				Log.Write("rl-bridge", "RL agent disconnected, scheduling graceful exit");
				ScheduleProcessExit("agent disconnected", 2000);
			}
		}

		/// <summary>
		/// Channel for the game thread to push observations to the gRPC stream.
		/// </summary>
		internal ChannelReader<RLProto.GameObservation> ObservationReader => observationChannel.Reader;

		/// <summary>
		/// Channel for the gRPC stream to push actions to the game thread.
		/// </summary>
		internal ChannelWriter<RLProto.AgentAction> ActionWriter => actionChannel.Writer;

		/// <summary>
		/// Snapshot all Mobile and Building actors into cached lists.
		/// Called once at advance start and refreshed every FullRefreshEveryNChecks.
		/// </summary>
		void SnapshotActors()
		{
			cachedMobileActors.Clear();
			cachedMobileActors.AddRange(world.ActorsHavingTrait<Mobile>());
			cachedBuildingActors.Clear();
			cachedBuildingActors.AddRange(world.ActorsHavingTrait<Building>());
		}

		/// <summary>
		/// Check all enabled interrupt signals against current world state.
		/// Returns the signal name if one fires, null otherwise.
		/// Uses cached actor references for fast iteration (no trait dictionary walk).
		/// Full trait re-enumeration every 4th check to catch newly spawned actors.
		/// </summary>
		string CheckInterrupts()
		{
			if (observationSerializer == null)
				return null;

			// Periodically refresh cached actor lists to catch new spawns (harvesters, produced units)
			interruptCheckCount++;
			if (interruptCheckCount >= FullRefreshEveryNChecks)
			{
				SnapshotActors();
				interruptCheckCount = 0;
			}

			// Build current state from cached actor references (fast — no trait lookup)
			curEnemyIds.Clear();
			curOwnUnitIds.Clear();
			curOwnUnitHp.Clear();
			curEnemyBuildingIds.Clear();
			curOwnBuildingIds.Clear();

			var shroud = player?.Shroud;

			foreach (var a in cachedMobileActors)
			{
				if (a.IsDead || !a.IsInWorld)
					continue;

				var owner = a.Owner;
				if (owner == player)
				{
					curOwnUnitIds.Add(a.ActorID);
					var health = a.TraitOrDefault<Health>();
					if (health != null)
						curOwnUnitHp[a.ActorID] = (float)health.HP / health.MaxHP;
				}
				else if (!owner.NonCombatant
					&& (shroud == null || shroud.IsVisible(a.CenterPosition)))
				{
					curEnemyIds.Add(a.ActorID);
				}
			}

			foreach (var a in cachedBuildingActors)
			{
				if (a.IsDead || !a.IsInWorld)
					continue;

				if (a.Owner == player)
					curOwnBuildingIds.Add(a.ActorID);
				else if (!a.Owner.NonCombatant
					&& (shroud == null || shroud.IsVisible(a.CenterPosition)))
					curEnemyBuildingIds.Add(a.ActorID);
			}

			float exploredPct = prevExploredPct;

			// On first check of a new advance, just populate prev-state without firing.
			bool isFirstCheck = prevVisibleEnemyIds.Count == 0 && prevOwnUnitIds.Count == 0;
			if (isFirstCheck)
			{
				UpdatePrevState(curEnemyIds, curOwnUnitIds, curOwnUnitHp,
					curEnemyBuildingIds, curOwnBuildingIds, exploredPct);
				return null;
			}

			// Check signals in priority order
			if (enabledInterrupts.Contains("game_over") && world.IsGameOver)
			{
				UpdatePrevState(curEnemyIds, curOwnUnitIds, curOwnUnitHp,
					curEnemyBuildingIds, curOwnBuildingIds, exploredPct);
				return "game_over";
			}

			if (enabledInterrupts.Contains("enemy_spotted"))
			{
				foreach (var id in curEnemyIds)
				{
					if (!prevVisibleEnemyIds.Contains(id))
					{
						UpdatePrevState(curEnemyIds, curOwnUnitIds, curOwnUnitHp,
							curEnemyBuildingIds, curOwnBuildingIds, exploredPct);
						return "enemy_spotted";
					}
				}
			}

			if (enabledInterrupts.Contains("unit_destroyed"))
			{
				foreach (var id in prevOwnUnitIds)
				{
					if (!curOwnUnitIds.Contains(id))
					{
						UpdatePrevState(curEnemyIds, curOwnUnitIds, curOwnUnitHp,
							curEnemyBuildingIds, curOwnBuildingIds, exploredPct);
						return "unit_destroyed";
					}
				}
			}

			if (enabledInterrupts.Contains("under_attack"))
			{
				foreach (var kvp in prevUnitHpPct)
				{
					if (curOwnUnitHp.TryGetValue(kvp.Key, out var currentHp))
					{
						if (kvp.Value - currentHp > 0.05f)
						{
							UpdatePrevState(curEnemyIds, curOwnUnitIds, curOwnUnitHp,
								curEnemyBuildingIds, curOwnBuildingIds, exploredPct);
							return "under_attack";
						}
					}
				}
			}

			if (enabledInterrupts.Contains("building_discovered"))
			{
				foreach (var id in curEnemyBuildingIds)
				{
					if (!prevEnemyBuildingIds.Contains(id))
					{
						UpdatePrevState(curEnemyIds, curOwnUnitIds, curOwnUnitHp,
							curEnemyBuildingIds, curOwnBuildingIds, exploredPct);
						return "building_discovered";
					}
				}
			}

			if (enabledInterrupts.Contains("enemy_building_destroyed"))
			{
				foreach (var id in prevEnemyBuildingIds)
				{
					if (!curEnemyBuildingIds.Contains(id))
					{
						UpdatePrevState(curEnemyIds, curOwnUnitIds, curOwnUnitHp,
							curEnemyBuildingIds, curOwnBuildingIds, exploredPct);
						return "enemy_building_destroyed";
					}
				}
			}

			if (enabledInterrupts.Contains("own_building_destroyed"))
			{
				foreach (var id in prevOwnBuildingIds)
				{
					if (!curOwnBuildingIds.Contains(id))
					{
						UpdatePrevState(curEnemyIds, curOwnUnitIds, curOwnUnitHp,
							curEnemyBuildingIds, curOwnBuildingIds, exploredPct);
						return "own_building_destroyed";
					}
				}
			}

			// Priority 3: unit_arrived (was moving, now idle)
			if (enabledInterrupts.Contains("unit_arrived"))
			{
				foreach (var a in cachedMobileActors)
				{
					if (a.IsDead || !a.IsInWorld || a.Owner != player)
						continue;
					if (prevMovingUnitIds.Contains(a.ActorID) && a.IsIdle)
					{
						UpdatePrevState(curEnemyIds, curOwnUnitIds, curOwnUnitHp,
							curEnemyBuildingIds, curOwnBuildingIds, exploredPct);
						UpdateMovingUnits();
						return "unit_arrived";
					}
				}
			}

			// Priority 5: production_complete (own unit count increased)
			if (enabledInterrupts.Contains("production_complete"))
			{
				if (curOwnUnitIds.Count > prevOwnUnitIds.Count)
				{
					UpdatePrevState(curEnemyIds, curOwnUnitIds, curOwnUnitHp,
						curEnemyBuildingIds, curOwnBuildingIds, exploredPct);
					UpdateMovingUnits();
					return "production_complete";
				}
			}

			if (enabledInterrupts.Contains("exploration_milestone"))
			{
				var prevBucket = (int)(prevExploredPct / 10f);
				var curBucket = (int)(exploredPct / 10f);
				if (curBucket > prevBucket)
				{
					UpdatePrevState(curEnemyIds, curOwnUnitIds, curOwnUnitHp,
						curEnemyBuildingIds, curOwnBuildingIds, exploredPct);
					UpdateMovingUnits();
					return "exploration_milestone";
				}
			}

			UpdatePrevState(curEnemyIds, curOwnUnitIds, curOwnUnitHp,
				curEnemyBuildingIds, curOwnBuildingIds, exploredPct);
			UpdateMovingUnits();
			return null;
		}

		void UpdatePrevState(HashSet<uint> enemyIds, HashSet<uint> ownUnitIds,
			Dictionary<uint, float> ownUnitHp, HashSet<uint> enemyBuildingIds,
			HashSet<uint> ownBuildingIds, float exploredPct)
		{
			prevVisibleEnemyIds.Clear();
			prevVisibleEnemyIds.UnionWith(enemyIds);
			prevOwnUnitIds.Clear();
			prevOwnUnitIds.UnionWith(ownUnitIds);
			prevUnitHpPct.Clear();
			foreach (var kvp in ownUnitHp)
				prevUnitHpPct[kvp.Key] = kvp.Value;
			prevEnemyBuildingIds.Clear();
			prevEnemyBuildingIds.UnionWith(enemyBuildingIds);
			prevOwnBuildingIds.Clear();
			prevOwnBuildingIds.UnionWith(ownBuildingIds);
			prevExploredPct = exploredPct;
		}

		void UpdateMovingUnits()
		{
			prevMovingUnitIds.Clear();
			foreach (var a in cachedMobileActors)
			{
				if (!a.IsDead && a.IsInWorld && a.Owner == player && !a.IsIdle)
					prevMovingUnitIds.Add(a.ActorID);
			}
		}

		/// <summary>
		/// Unary FastAdvance: submit commands, fast-forward N ticks, return observation.
		/// In multi-session mode, acquires a worker slot and ticks inline on the
		/// calling (gRPC) thread. Returns RESOURCE_EXHAUSTED if all workers are busy.
		/// In single-session mode, routes through the action channel to the game thread.
		/// </summary>
		internal async Task<RLProto.GameObservation> RequestFastAdvance(
			int ticks, IEnumerable<RLProto.Command> commands, CancellationToken ct,
			int checkEventsEvery = 0, IEnumerable<string> enabledInterruptNames = null)
		{
			// Fail fast if session is already dead
			if (SessionDone.IsSet)
				throw new RpcException(new Status(StatusCode.Aborted,
					$"Session {episodeId} is no longer active"));

			// ticks <= 0: GUI skirmish. Inject commands, return current obs,
			// do NOT set tickScale (the human is playing in real time).
			if (ticks <= 0)
				return await RequestRealtimeSnapshot(commands, ct);

			// Connect agent if this is the first call (unpauses game)
			if (!agentConnected)
				OnAgentConnected();

			var n = Math.Max(1, Math.Min(ticks, 5000));

			// Configure server-side interrupt detection
			interruptCheckInterval = Math.Max(0, checkEventsEvery);
			fastAdvanceStartTick = world.WorldTick;
			interruptNextCheckTick = world.WorldTick + Math.Max(interruptCheckInterval, 1);
			pendingInterruptReason = null;
			interruptCheckCount = 0;
			enabledInterrupts.Clear();
			if (enabledInterruptNames != null)
				foreach (var name in enabledInterruptNames)
					enabledInterrupts.Add(name);

			// Snapshot actors once at advance start — CheckInterrupts uses cached lists
			// instead of expensive ActorsHavingTrait calls on every check
			if (interruptCheckInterval > 0)
			{
				SnapshotActors();
				Console.Error.WriteLine($"[rl-bridge] FastAdvance {n} ticks with interrupt check every {interruptCheckInterval} ticks, {enabledInterrupts.Count} signals, {cachedMobileActors.Count} mobile + {cachedBuildingActors.Count} buildings cached (session {episodeId})");
			}

			// Set up the TCS before queueing — worker will complete it via ITick.Tick.
			// Keep a local reference because ITick.Tick nulls the field after completion.
			var tcs = new TaskCompletionSource<RLProto.GameObservation>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			pendingAdvanceResult = tcs;

			// Build a single AgentAction with user commands + FAST_ADVANCE
			var action = new RLProto.AgentAction();
			var cmds = commands.ToList();
			if (cmds.Count > 0)
				action.Commands.Add(cmds);

			action.Commands.Add(new RLProto.Command
			{
				Action = RLProto.ActionType.FastAdvance,
				Ticks = n,
			});
			actionChannel.Writer.TryWrite(action);

			if (MultiSessionMode)
			{
				// Wait briefly for session state — it may still be registering
				// if FastAdvance arrives right after the bridge activates during
				// World creation but before InitSession registers SessionStates.
				RLSessionManager.SessionState state = null;
				for (var retry = 0; retry < 50; retry++)
				{
					if (RLSessionManager.SessionStates.TryGetValue(episodeId, out state))
						break;
					Thread.Sleep(100);
				}

				if (state == null)
					throw new RpcException(new Status(StatusCode.NotFound,
						$"Session state not found for {episodeId}"));

				var workItem = RLSessionManager.SubmitWork(state, this);
				if (workItem == null)
					throw new RpcException(new Status(StatusCode.ResourceExhausted,
						"All worker slots busy, retry later"));

				using var reg = ct.Register(() =>
				{
					tcs.TrySetCanceled();
					workItem.Completed.TrySetCanceled();
				});

				// Wait for the worker to finish ticking (non-blocking await) —
				// with a timeout proportional to requested ticks (min 120s, up to 900s)
				// to support heavy simulations with multiple concurrent sessions.
				var fastAdvanceTimeoutMs = Math.Max(120_000, Math.Min(900_000, n * 5_000));
				try
				{
					await workItem.Completed.Task.WaitAsync(TimeSpan.FromMilliseconds(
						fastAdvanceTimeoutMs), ct);
				}
				catch (Exception ex) when (ex is TimeoutException
					or OperationCanceledException)
				{
					// Cancel the queued work so the session doesn't stay
					// wedged; clear the pending TCS as well.
					workItem.Completed.TrySetCanceled();
					tcs.TrySetCanceled();
					SessionDone.Set();
					throw new RpcException(new Status(StatusCode.DeadlineExceeded,
						$"FastAdvance timeout ({fastAdvanceTimeoutMs}ms) for session "
						+ $"{episodeId} (tick {world.WorldTick})"));
				}
				return await tcs.Task;
			}
			else
			{
				// Legacy single-session mode: delegate to the game thread
				TickRequested.Set();

				using var reg = ct.Register(() => tcs.TrySetCanceled());
				return await tcs.Task;
			}
		}

		/// <summary>
		/// Inject optional commands on the game thread and return the current
		/// observation without fast-forwarding. First call unpauses the match.
		/// </summary>
		async Task<RLProto.GameObservation> RequestRealtimeSnapshot(
			IEnumerable<RLProto.Command> commands, CancellationToken ct)
		{
			if (!agentConnected)
				OnAgentConnected();

			var tcs = new TaskCompletionSource<RLProto.GameObservation>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			pendingAdvanceResult = tcs;
			pendingRealtimeSnapshot = true;

			var cmds = commands != null ? commands.ToList() : new List<RLProto.Command>();
			if (cmds.Count > 0)
			{
				var action = new RLProto.AgentAction();
				action.Commands.Add(cmds);
				actionChannel.Writer.TryWrite(action);
			}

			TickRequested.Set();
			using var reg = ct.Register(() => tcs.TrySetCanceled());
			return await tcs.Task;
		}

		/// <summary>
		/// Headless train: kill the process so replays flush. GUI skirmish: keep
		/// the window open for the victory/defeat screen.
		/// </summary>
		void ScheduleProcessExit(string reason, int delayMs)
		{
			TryStopPpoSidecar();
			if (!Game.IsHeadless)
			{
				Log.Write("rl-bridge", $"{reason} (GUI: keeping process alive)");
				return;
			}

			new Thread(() =>
			{
				Thread.Sleep(Math.Max(0, delayMs));
				Log.Write("rl-bridge", $"Exiting game process ({reason})");
				Game.Exit();
			})
			{
				IsBackground = true,
				Name = "RL-Bridge-Exit"
			}.Start();
		}

		void TryStartPpoSidecar(int port)
		{
			var disable = Environment.GetEnvironmentVariable("OPENRA_RL_AUTOSTART");
			if (disable == "0" || string.Equals(disable, "false", StringComparison.OrdinalIgnoreCase))
			{
				Log.Write("rl-bridge", "PPO sidecar: OPENRA_RL_AUTOSTART=0 (external attach)");
				return;
			}

			var repo = FindRlRepo();
			if (repo == null)
			{
				Log.Write("rl-bridge", "PPO sidecar: repo not found (set OPENRA_RL_ROOT to OpenRA-RL)");
				return;
			}

			var python = Environment.GetEnvironmentVariable("OPENRA_RL_PYTHON");
			if (string.IsNullOrEmpty(python))
			{
				var win = Path.Combine(repo, ".venv", "Scripts", "python.exe");
				var nix = Path.Combine(repo, ".venv", "bin", "python");
				python = File.Exists(win) ? win : File.Exists(nix) ? nix : null;
			}

			if (string.IsNullOrEmpty(python) || !File.Exists(python))
			{
				Log.Write("rl-bridge", "PPO sidecar: python not found (set OPENRA_RL_PYTHON)");
				return;
			}

			var ckpt = Environment.GetEnvironmentVariable("OPENRA_RL_CKPT");
			if (string.IsNullOrEmpty(ckpt))
			{
				var best = Path.Combine(repo, "rl", "ckpts", "best.pt");
				var latest = Path.Combine(repo, "rl", "ckpts", "latest.pt");
				ckpt = File.Exists(best) ? best : latest;
			}

			if (string.IsNullOrEmpty(ckpt) || !File.Exists(ckpt))
			{
				Log.Write("rl-bridge", $"PPO sidecar: checkpoint missing ({ckpt})");
				return;
			}

			try
			{
				// OpenRA.Log.Write throws on the logging thread if the channel
				// was never AddChannel'd (kills the whole process). Register
				// here too in case this binary is mixed with an older Game.dll.
				Log.AddChannel("ppo-agent", "ppo-agent.log");

				var psi = new ProcessStartInfo
				{
					FileName = python,
					Arguments = $"-m rl.play_skirmish --attach --port {port} --ckpt \"{ckpt}\"",
					WorkingDirectory = repo,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true
				};
				psi.Environment["PYTHONPATH"] = repo;
				psi.Environment["PYTHONUNBUFFERED"] = "1";
				psi.Environment["OPENRA_RL_AUTOSTART"] = "0";

				ppoSidecar = Process.Start(psi);
				if (ppoSidecar == null)
				{
					Log.Write("rl-bridge", "PPO sidecar: Process.Start returned null");
					return;
				}

				ppoSidecar.OutputDataReceived += (_, e) =>
				{
					if (!string.IsNullOrEmpty(e.Data))
						Log.Write("ppo-agent", e.Data);
				};
				ppoSidecar.ErrorDataReceived += (_, e) =>
				{
					if (!string.IsNullOrEmpty(e.Data))
						Log.Write("ppo-agent", e.Data);
				};
				ppoSidecar.BeginOutputReadLine();
				ppoSidecar.BeginErrorReadLine();
				Log.Write("rl-bridge", $"PPO sidecar pid={ppoSidecar.Id} ckpt={ckpt}");
			}
			catch (Exception e)
			{
				Log.Write("rl-bridge", $"PPO sidecar failed to start: {e}");
			}
		}

		void TryStopPpoSidecar()
		{
			var proc = ppoSidecar;
			ppoSidecar = null;
			if (proc == null || proc.HasExited)
				return;

			try
			{
				proc.Kill(entireProcessTree: true);
				Log.Write("rl-bridge", $"PPO sidecar killed pid={proc.Id}");
			}
			catch (Exception e)
			{
				Log.Write("rl-bridge", $"PPO sidecar kill: {e.Message}");
			}
		}

		static string FindRlRepo()
		{
			var env = Environment.GetEnvironmentVariable("OPENRA_RL_ROOT");
			if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "rl", "play_skirmish.py")))
				return Path.GetFullPath(env);

			var engine = Platform.EngineDir;
			foreach (var cand in new[] { Path.Combine(engine, ".."), engine })
			{
				var full = Path.GetFullPath(cand);
				if (File.Exists(Path.Combine(full, "rl", "play_skirmish.py")))
					return full;
			}

			return null;
		}

		/// <summary>
		/// Get a snapshot of the current game state for unary GetState RPC.
		/// </summary>
		internal RLProto.GameState GetCurrentState()
		{
			var phase = !IsEnabled ? "waiting"
				: world.IsGameOver ? "game_over"
				: "playing";

			var winner = "";
			if (world.IsGameOver)
			{
				foreach (var p in world.Players)
				{
					if (p.WinState == WinState.Won)
					{
						winner = p.InternalName;
						break;
					}
				}
			}

			// Player and enemy faction
			var playerFaction = player?.Faction?.InternalName ?? "";
			var enemyFaction = "";
			foreach (var p in world.Players)
			{
				if (p != player && !p.NonCombatant && p.Playable)
				{
					enemyFaction = p.Faction?.InternalName ?? "";
					break;
				}
			}

			return new RLProto.GameState
			{
				EpisodeId = episodeId,
				Tick = world.WorldTick,
				Phase = phase,
				Winner = winner,
				PlayerCount = world.Players.Length,
				PlayerFaction = playerFaction,
				EnemyFaction = enemyFaction,
			};
		}

		/// <summary>
		/// Look up a session by ID. If sessionId is empty, returns the first (only) session
		/// for backward compatibility with single-session mode.
		/// </summary>
		internal static ExternalBotBridge LookupSession(string sessionId)
		{
			if (!string.IsNullOrEmpty(sessionId))
			{
				Sessions.TryGetValue(sessionId, out var bridge);
				return bridge;
			}

			// Legacy fallback: return first available session
			foreach (var kvp in Sessions)
				return kvp.Value;

			return null;
		}
	}
}
