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
using System.Linq;
using System.Threading;
using OpenRA.Traits;
using RLProto = OpenRA.Mods.Common.RL;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>
	/// v3/v4: runs the rl-agent player as a modular bot (the normal AI's modules, enabled for
	/// rl-agent in rules) whose strategic decisions come from the agent.
	/// Brain "external": BUILD / TRAIN / ARMY (MAIN, HARASS, AIR) / SUPPORT requests go to the agent.
	/// Brain "builtin": the modules' own heuristics decide and are reported as teacher decisions.
	/// Owned and ticked by <see cref="ExternalBotBridge"/> on the world thread.
	/// </summary>
	sealed class RLCommander
	{
		// Re-ask a queue this long after "wait" (or an unanswered request).
		const int WaitTicks = 100;
		const int SupportWaitTicks = 150;
		const int SupportCheckInterval = 25;
		const int ArmyInterval = 250;
		const int MaxTargets = 16;
		const int MaxMobileTargets = 4;
		const int MaxSectorTargets = 4;
		const int MobileMemoryTicks = 300;
		const int StaleTicks = 2000;
		const int DedupCells = 6;
		const int HarvesterDedupCells = 3;
		const int ClearedRadius = 10;
		const int SectorCells = 16;
		static readonly HashSet<string> GroundTerrain = ["Clear", "Road", "Rough", "Ore", "Gems", "Beach", "Bridge"];
		static readonly WDist TargetRadius = WDist.FromCells(10);

		public readonly bool External;

		readonly IBot bot;
		readonly World world;
		readonly Player player;
		readonly IBotTick[] tickModules;
		readonly IBotRespondToAttack[] attackResponseModules;

		// Written by the gRPC thread, drained on the world thread when a new step starts.
		readonly ConcurrentQueue<RLProto.Decision> inbox = new();
		int submittedSteps;
		int appliedSteps;

		readonly Dictionary<uint, RLProto.DecisionRequest> outstanding = [];
		readonly Dictionary<uint, ProductionQueue> requestQueues = [];
		readonly Dictionary<uint, string> requestSupportKeys = [];
		readonly Dictionary<uint, string> requestWaitKeys = [];
		readonly Dictionary<string, uint> requestByKey = [];
		readonly Dictionary<string, int> waitUntil = [];
		readonly List<RLProto.DecisionRequest> newRequests = [];
		readonly List<RLProto.TeacherDecision> teacher = [];
		readonly Dictionary<uint, (CPos Cell, string Type)> seenEnemyBuildings = [];
		readonly Dictionary<uint, (CPos Cell, string Type, int Tick, bool IsHarvester, int Cost)> seenEnemyUnits = [];
		readonly Dictionary<(int X, int Y), int> sectorLastSeen = [];
		List<CPos> cachedMapSpawns;
		uint nextId = 1;
		int nextArmyTick = ArmyInterval;
		int nextSupportTick = SupportCheckInterval;

		public RLCommander(IBot bot, World world, Player player, bool external)
		{
			this.bot = bot;
			this.world = world;
			this.player = player;
			External = external;

			tickModules = player.PlayerActor.TraitsImplementing<IBotTick>().ToArray();
			attackResponseModules = player.PlayerActor.TraitsImplementing<IBotRespondToAttack>().ToArray();
			foreach (var ibe in player.PlayerActor.TraitsImplementing<IBotEnabled>())
				ibe.BotEnabled(bot);
		}

		/// <summary>
		/// New requests (external) or teacher decisions (builtin) since the last observation.
		/// The advance stops on them, so each decision comes with the state it was taken in.
		/// </summary>
		public bool HasNewRequests => External ? newRequests.Count > 0 : teacher.Count > 0;

		/// <summary>gRPC thread: answers sent with a FastAdvance (possibly none).</summary>
		public void Submit(IEnumerable<RLProto.Decision> decisions)
		{
			if (decisions != null)
				foreach (var d in decisions)
					inbox.Enqueue(d);

			Interlocked.Increment(ref submittedSteps);
		}

		public void Tick()
		{
			var steps = Volatile.Read(ref submittedSteps);
			if (steps != appliedSteps)
			{
				appliedSteps = steps;
				BeginStep();
			}

			Sync.RunUnsynced(Game.Settings.Debug.SyncCheckBotModuleCode, world, () =>
			{
				foreach (var t in tickModules)
					if (t.IsTraitEnabled())
						t.BotTick(bot);
			});

			TickArmy();
			TickSupport();
		}

		public void Damaged(Actor self, AttackInfo e)
		{
			Sync.RunUnsynced(Game.Settings.Debug.SyncCheckBotModuleCode, world, () =>
			{
				foreach (var t in attackResponseModules)
					if (t.IsTraitEnabled())
						t.RespondToAttack(bot, self, e);
			});
		}

		/// <summary>Move new requests, teacher decisions, and v4 metadata into an outgoing observation.</summary>
		public void FillObservation(RLProto.GameObservation obs)
		{
			obs.PendingDecisions.Add(newRequests);
			newRequests.Clear();
			obs.TeacherDecisions.Add(teacher);
			teacher.Clear();

			var squads = Squads();
			if (squads != null)
				obs.HarassUnitIds.AddRange(squads.RLHarassActorIds());

			var baseBuilder = BaseBuilder();
			if (baseBuilder != null)
				obs.PlacementFailures = baseBuilder.TotalPlacementFailures;
		}

		void BeginStep()
		{
			while (inbox.TryDequeue(out var d))
			{
				if (!outstanding.Remove(d.RequestId, out var req))
					continue;

				var wKey = requestWaitKeys.GetValueOrDefault(d.RequestId, KeyOf(req));
				if (req.Kind == RLProto.DecisionKind.DecideArmy)
					ApplyArmy(req, d);
				else if (req.Kind == RLProto.DecisionKind.DecideSupport)
				{
					if (!ApplySupport(req, requestSupportKeys.GetValueOrDefault(d.RequestId), d))
						waitUntil[wKey] = world.WorldTick + SupportWaitTicks;
				}
				else if (!StartProduction(requestQueues.GetValueOrDefault(d.RequestId), d.Choice))
					waitUntil[wKey] = world.WorldTick + WaitTicks;
			}

			// Requests the agent did not answer count as "wait" (or "keep" for ARMY).
			foreach (var req in outstanding.Values)
			{
				var wKey = requestWaitKeys.GetValueOrDefault(req.Id, KeyOf(req));
				if (req.Kind == RLProto.DecisionKind.DecideSupport)
					waitUntil[wKey] = world.WorldTick + SupportWaitTicks;
				else if (req.Kind != RLProto.DecisionKind.DecideArmy)
					waitUntil[wKey] = world.WorldTick + WaitTicks;
			}

			outstanding.Clear();
			requestQueues.Clear();
			requestSupportKeys.Clear();
			requestWaitKeys.Clear();
			requestByKey.Clear();
		}

		/// <summary>
		/// Start the agent's choice right away (no extra module polling latency), if the
		/// queue is still free and the item still buildable. False means "nothing started".
		/// </summary>
		bool StartProduction(ProductionQueue queue, string choice)
		{
			if (string.IsNullOrEmpty(choice) || queue == null || queue.Actor.IsDead || !queue.Actor.IsInWorld
				|| queue.AllQueued().Any() || !queue.BuildableItems().Any(a => a.Name == choice))
				return false;

			bot.QueueOrder(Order.StartProduction(queue.Actor, choice, 1));
			return true;
		}

		static string KeyOf(RLProto.DecisionRequest req)
		{
			var q = req.Kind == RLProto.DecisionKind.DecideArmy && string.IsNullOrEmpty(req.Queue) ? "MAIN" : req.Queue;
			return $"{req.Kind}:{q}";
		}

		RLProto.DecisionRequest NewRequest(RLProto.DecisionKind kind, string queue)
		{
			return new RLProto.DecisionRequest { Id = nextId++, Kind = kind, Queue = queue ?? "", Tick = world.WorldTick };
		}

		void Ask(RLProto.DecisionRequest req, string customKey = null)
		{
			var key = customKey ?? KeyOf(req);
			outstanding[req.Id] = req;
			requestWaitKeys[req.Id] = key;
			requestByKey[key] = req.Id;
			newRequests.Add(req);
		}

		public ActorInfo DecideProduction(BotDecisionKind kind, ProductionQueue queue, Func<ActorInfo> nativeChoice)
		{
			var protoKind = kind == BotDecisionKind.Build ? RLProto.DecisionKind.DecideBuild : RLProto.DecisionKind.DecideTrain;
			var key = $"{protoKind}:{queue.Info.Type}";

			if (requestByKey.ContainsKey(key) || (waitUntil.TryGetValue(key, out var until) && world.WorldTick < until))
				return null;

			var options = queue.BuildableItems().Select(a => a.Name).ToList();
			if (options.Count == 0)
				return null;

			var request = NewRequest(protoKind, queue.Info.Type);
			request.Options.Add(options);
			requestQueues[request.Id] = queue;

			if (External)
			{
				Ask(request, key);
				return null;
			}

			// Builtin: the native choice is answered through the same path as an agent's
			// (applied when the next step starts), so the teacher decision is paired with
			// the exact state an external agent would see, before production started.
			var decision = new RLProto.Decision { RequestId = request.Id, Choice = nativeChoice()?.Name ?? "" };
			outstanding[request.Id] = request;
			requestWaitKeys[request.Id] = key;
			requestByKey[key] = request.Id;
			inbox.Enqueue(decision);
			teacher.Add(new RLProto.TeacherDecision { Request = request, Decision = decision });
			return null;
		}

		SquadManagerBotModule Squads()
		{
			return player.PlayerActor.TraitsImplementing<SquadManagerBotModule>().FirstOrDefault(m => m.IsTraitEnabled());
		}

		BaseBuilderBotModule BaseBuilder()
		{
			return player.PlayerActor.TraitsImplementing<BaseBuilderBotModule>().FirstOrDefault(m => m.IsTraitEnabled());
		}

		void EmitArmyGroup(string group, List<RLProto.TargetCandidate> targets, SquadManagerBotModule squads)
		{
			var req = NewRequest(RLProto.DecisionKind.DecideArmy, group);
			req.Targets.Add(targets);
			var key = KeyOf(req);

			if (External)
			{
				if (!requestByKey.ContainsKey(key))
					Ask(req, key);
				return;
			}

			var target = squads.RLArmyTarget(group);
			var index = -1;
			if (target != null && req.Targets.Count > 0)
			{
				var best = long.MaxValue;
				for (var i = 0; i < req.Targets.Count; i++)
				{
					var t = req.Targets[i];
					var d = (new CPos(t.CellX, t.CellY) - target.Location).LengthSquared;
					if (d < best)
					{
						best = d;
						index = i;
					}
				}
			}

			teacher.Add(new RLProto.TeacherDecision
			{
				Request = req,
				Decision = new RLProto.Decision { RequestId = req.Id, TargetIndex = index },
			});
		}

		void TickArmy()
		{
			if (world.WorldTick < nextArmyTick)
				return;

			nextArmyTick = world.WorldTick + ArmyInterval;
			var squads = Squads();
			if (squads == null)
				return;

			UpdateEnemyMemory();
			var targets = BuildTargets();

			EmitArmyGroup("MAIN", targets, squads);
			if (External && squads.RLCanHarass())
				EmitArmyGroup("HARASS", targets, squads);
			if (squads.RLHasAir())
				EmitArmyGroup("AIR", targets, squads);
		}

		void TickSupport()
		{
			if (!External || world.WorldTick < nextSupportTick)
				return;

			nextSupportTick = world.WorldTick + SupportCheckInterval;
			var spm = player.PlayerActor.TraitOrDefault<SupportPowerManager>();
			if (spm == null || spm.Powers.Count == 0)
				return;

			List<RLProto.TargetCandidate> targets = null;
			foreach (var kv in spm.Powers.OrderBy(p => p.Key, StringComparer.Ordinal))
			{
				var sp = kv.Value;
				if (sp == null || sp.Disabled || !sp.Ready)
					continue;

				var orderName = sp.Info?.OrderName ?? kv.Key;
				if (orderName.Contains("Gps", StringComparison.OrdinalIgnoreCase))
					continue;

				var key = $"{RLProto.DecisionKind.DecideSupport}:{kv.Key}";
				if (requestByKey.ContainsKey(key) || (waitUntil.TryGetValue(key, out var until) && world.WorldTick < until))
					continue;

				if (targets == null)
				{
					UpdateEnemyMemory();
					targets = BuildTargets();
				}

				if (targets.Count == 0)
					continue;

				var req = NewRequest(RLProto.DecisionKind.DecideSupport, orderName);
				req.Targets.Add(targets);
				requestSupportKeys[req.Id] = kv.Key;
				Ask(req, key);
			}
		}

		void ApplyArmy(RLProto.DecisionRequest req, RLProto.Decision d)
		{
			var squads = Squads();
			if (squads == null)
				return;

			var group = string.IsNullOrEmpty(req.Queue) ? "MAIN" : req.Queue;
			var index = d.TargetIndex;
			if (index < 0 || index >= req.Targets.Count)
			{
				squads.RLHoldGroup(bot, group);
				return;
			}

			var t = req.Targets[index];
			var cell = new CPos(t.CellX, t.CellY);
			if (string.Equals(group, "HARASS", StringComparison.OrdinalIgnoreCase))
				squads.RLAttackHarass(bot, cell, TargetRadius);
			else if (string.Equals(group, "AIR", StringComparison.OrdinalIgnoreCase))
				squads.RLAttackAir(bot, cell, TargetRadius);
			else
			{
				var fearless = string.Equals(d.Choice, "all_in", StringComparison.OrdinalIgnoreCase);
				squads.RLAttackMain(bot, cell, TargetRadius, fearless);
			}
		}

		bool ApplySupport(RLProto.DecisionRequest req, string spKey, RLProto.Decision d)
		{
			if (d.TargetIndex < 0 || d.TargetIndex >= req.Targets.Count || string.IsNullOrEmpty(spKey))
				return false;

			var spm = player.PlayerActor.TraitOrDefault<SupportPowerManager>();
			if (spm == null || !spm.Powers.TryGetValue(spKey, out var sp) || sp == null || sp.Disabled || !sp.Ready)
				return false;

			var t = req.Targets[d.TargetIndex];
			var dest = new CPos(t.CellX, t.CellY);
			if (!world.Map.Contains(dest))
				return false;

			var orderName = req.Queue ?? sp.Info?.OrderName ?? spKey;

			// Iron Curtain grants invulnerability to friendly armored units; snap destination to our lead combat vehicle.
			if (orderName.Contains("GrantExternalCondition", StringComparison.OrdinalIgnoreCase))
			{
				var ownTank = world.ActorsHavingTrait<Mobile>()
					.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld
						&& a.TraitOrDefault<Harvester>() == null
						&& !string.Equals(a.Info.Name, "mcv", StringComparison.OrdinalIgnoreCase)
						&& a.Info.HasTraitInfo<AttackBaseInfo>())
					.OrderByDescending(a => a.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0)
					.ThenBy(a => (a.Location - dest).LengthSquared)
					.FirstOrDefault();
				if (ownTank == null)
					return false;
				dest = ownTank.Location;
			}

			var order = new Order(spKey, spm.Self, Target.FromCell(world, dest), false)
			{
				SuppressVisualFeedback = true,
				ExtraData = uint.MaxValue,
			};

			if (orderName.Contains("Chronoshift", StringComparison.OrdinalIgnoreCase))
			{
				var sourceUnit = world.ActorsHavingTrait<Mobile>()
					.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld
						&& a.TraitOrDefault<Harvester>() == null
						&& !string.Equals(a.Info.Name, "mcv", StringComparison.OrdinalIgnoreCase)
						&& a.Info.HasTraitInfo<AttackBaseInfo>())
					.OrderByDescending(a => a.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0)
					.ThenBy(a => a.ActorID)
					.FirstOrDefault();
				if (sourceUnit == null)
					return false;
				order.ExtraLocation = sourceUnit.Location;
			}

			bot.QueueOrder(order);
			return true;
		}

		/// <summary>Enemy buildings and recent mobile units the agent has seen under fog-of-war rules.</summary>
		void UpdateEnemyMemory()
		{
			foreach (var a in world.ActorsHavingTrait<Building>())
			{
				if (a.IsDead || !a.IsInWorld || player.RelationshipWith(a.Owner) != PlayerRelationship.Enemy
					|| a.Info.HasTraitInfo<LineBuildInfo>() || !a.CanBeViewedByPlayer(player))
					continue;

				seenEnemyBuildings[a.ActorID] = (a.Location, a.Info.Name);
			}

			var goneBld = new List<uint>();
			foreach (var (id, seen) in seenEnemyBuildings)
			{
				var a = world.GetActorById(id);
				if ((a == null || a.IsDead || !a.IsInWorld) && player.Shroud.IsVisible(seen.Cell))
					goneBld.Add(id);
			}

			foreach (var id in goneBld)
				seenEnemyBuildings.Remove(id);

			foreach (var a in world.ActorsHavingTrait<IPositionable>())
			{
				if (a.IsDead || !a.IsInWorld || a.Owner == null || a.Owner.NonCombatant
					|| player.RelationshipWith(a.Owner) != PlayerRelationship.Enemy
					|| a.Info.HasTraitInfo<BuildingInfo>() || a.Info.HasTraitInfo<HuskInfo>()
					|| a.Info.HasTraitInfo<AircraftInfo>() || !a.CanBeViewedByPlayer(player))
					continue;

				var isHarv = a.TraitOrDefault<Harvester>() != null
					|| string.Equals(a.Info.Name, "harv", StringComparison.OrdinalIgnoreCase);
				var cost = a.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
				if (!isHarv && !a.Info.HasTraitInfo<AttackBaseInfo>() && !string.Equals(a.Info.Name, "mcv", StringComparison.OrdinalIgnoreCase))
					continue;

				seenEnemyUnits[a.ActorID] = (a.Location, a.Info.Name, world.WorldTick, isHarv, cost);
			}

			var goneUnits = new List<uint>();
			foreach (var (id, seen) in seenEnemyUnits)
			{
				if (world.WorldTick - seen.Tick > MobileMemoryTicks)
				{
					goneUnits.Add(id);
					continue;
				}

				var a = world.GetActorById(id);
				if ((a == null || a.IsDead || !a.IsInWorld) && player.Shroud.IsVisible(seen.Cell))
					goneUnits.Add(id);
			}

			foreach (var id in goneUnits)
				seenEnemyUnits.Remove(id);
		}

		List<CPos> GetCandidateMapSpawns()
		{
			if (cachedMapSpawns != null)
				return cachedMapSpawns;

			cachedMapSpawns = [];
			foreach (var n in world.Map.ActorDefinitions)
			{
				if (n.Value.Value == "mpspawn")
				{
					var cell = new ActorReference(n.Key, n.Value).GetValue<LocationInit, CPos>();
					if ((cell - player.HomeLocation).LengthSquared > DedupCells * DedupCells)
						cachedMapSpawns.Add(cell);
				}
			}

			// Fallback for synthetic test scenarios that omit mpspawn actor definitions.
			if (cachedMapSpawns.Count == 0)
			{
				foreach (var p in world.Players)
				{
					if (!p.NonCombatant && player.RelationshipWith(p) == PlayerRelationship.Enemy
						&& (p.HomeLocation - player.HomeLocation).LengthSquared > DedupCells * DedupCells)
						cachedMapSpawns.Add(p.HomeLocation);
				}
			}

			var home = player.HomeLocation;
			cachedMapSpawns.Sort((a, b) =>
			{
				var da = (a - home).LengthSquared;
				var db = (b - home).LengthSquared;
				var cmp = da.CompareTo(db);
				if (cmp != 0)
					return cmp;
				cmp = a.X.CompareTo(b.X);
				return cmp != 0 ? cmp : a.Y.CompareTo(b.Y);
			});
			return cachedMapSpawns;
		}

		/// <summary>
		/// Map spawn candidates (until explored and found empty), remembered enemy buildings
		/// nearest our home, recent enemy harvesters / combat columns, and unexplored/stale
		/// map sectors. 100% fog-honest (never reads enemyPlayer.HomeLocation on standard maps).
		/// </summary>
		List<RLProto.TargetCandidate> BuildTargets()
		{
			var targets = new List<RLProto.TargetCandidate>();
			bool TooClose(CPos c, int radius = DedupCells) =>
				targets.Any(t => (new CPos(t.CellX, t.CellY) - c).LengthSquared <= radius * radius);
			bool KnownBuildingNear(CPos c) =>
				seenEnemyBuildings.Values.Any(s => (s.Cell - c).LengthSquared <= ClearedRadius * ClearedRadius);

			foreach (var spawn in GetCandidateMapSpawns())
			{
				if (targets.Count >= MaxTargets)
					break;
				if ((!player.Shroud.IsExplored(spawn) || KnownBuildingNear(spawn)) && !TooClose(spawn))
					targets.Add(new RLProto.TargetCandidate { CellX = spawn.X, CellY = spawn.Y, Kind = "enemy_spawn" });
			}

			var home = player.HomeLocation;
			var orderedBuildings = seenEnemyBuildings.Values
				.OrderBy(s => (s.Cell - home).LengthSquared)
				.ThenBy(s => s.Cell.X)
				.ThenBy(s => s.Cell.Y)
				.ToList();

			var mobileCandidates = seenEnemyUnits.Values
				.OrderByDescending(u => u.IsHarvester)
				.ThenByDescending(u => u.Cost)
				.ThenBy(u => (u.Cell - home).LengthSquared)
				.ThenBy(u => u.Cell.X)
				.ThenBy(u => u.Cell.Y)
				.ToList();

			var sectors = SectorTargets();
			var reserveMobile = Math.Min(MaxMobileTargets, mobileCandidates.Count);
			var reserveSector = Math.Min(MaxSectorTargets, sectors.Count);
			var buildingCap = Math.Max(0, MaxTargets - reserveMobile - reserveSector);

			var leftoverBuildings = new List<(CPos Cell, string Type)>();
			foreach (var seen in orderedBuildings)
			{
				if (targets.Count < buildingCap)
				{
					if (!TooClose(seen.Cell))
						targets.Add(new RLProto.TargetCandidate { CellX = seen.Cell.X, CellY = seen.Cell.Y, Kind = "enemy_building", Type = seen.Type });
				}
				else
					leftoverBuildings.Add(seen);
			}

			var addedMobile = 0;
			foreach (var mob in mobileCandidates)
			{
				if (addedMobile >= MaxMobileTargets || targets.Count >= MaxTargets - reserveSector)
					break;

				var dedup = mob.IsHarvester ? HarvesterDedupCells : DedupCells;
				if (!TooClose(mob.Cell, dedup))
				{
					targets.Add(new RLProto.TargetCandidate
					{
						CellX = mob.Cell.X,
						CellY = mob.Cell.Y,
						Kind = mob.IsHarvester ? "enemy_harvester" : "enemy_mobile",
						Type = mob.Type,
					});
					addedMobile++;
				}
			}

			foreach (var (cell, kind) in sectors)
			{
				if (targets.Count >= MaxTargets)
					break;

				if (!TooClose(cell))
					targets.Add(new RLProto.TargetCandidate { CellX = cell.X, CellY = cell.Y, Kind = kind });
			}

			foreach (var seen in leftoverBuildings)
			{
				if (targets.Count >= MaxTargets)
					break;

				if (!TooClose(seen.Cell))
					targets.Add(new RLProto.TargetCandidate { CellX = seen.Cell.X, CellY = seen.Cell.Y, Kind = "enemy_building", Type = seen.Type });
			}

			return targets;
		}

		/// <summary>
		/// Map sectors (SectorCells x SectorCells) worth sending the army to look at, nearest our
		/// home first: "unexplored" (less than half explored) then "stale" (explored but not seen
		/// for StaleTicks: an enemy may have built there since). The cell is a ground cell close
		/// to the sector center that a tank can reach from home (unreachable ones trap the army).
		/// </summary>
		List<(CPos Cell, string Kind)> SectorTargets()
		{
			var unexploredSectors = new List<CPos>();
			var staleSectors = new List<CPos>();
			var bounds = world.Map.Bounds;
			var pathFinder = world.WorldActor.TraitOrDefault<IPathFinder>();
			var tracked = world.WorldActor.TraitsImplementing<Locomotor>().FirstOrDefault(l => l.Info.Name == "tracked");
			bool Reachable(CPos c) => pathFinder == null || tracked == null
				|| pathFinder.PathExistsForLocomotor(tracked, player.HomeLocation, c);

			for (var sy = bounds.Top; sy < bounds.Bottom; sy += SectorCells)
			{
				for (var sx = bounds.Left; sx < bounds.Right; sx += SectorCells)
				{
					var explored = new List<CPos>();
					var unexplored = new List<CPos>();
					var visible = false;
					var center = new CPos(sx + SectorCells / 2, sy + SectorCells / 2);
					for (var y = sy; y < Math.Min(sy + SectorCells, bounds.Bottom); y += 2)
					{
						for (var x = sx; x < Math.Min(sx + SectorCells, bounds.Right); x += 2)
						{
							var cell = new MPos(x, y).ToCPos(world.Map);
							if (!world.Map.Contains(cell) || !GroundTerrain.Contains(world.Map.GetTerrainInfo(cell).Type))
								continue;

							if (!player.Shroud.IsExplored(cell))
								unexplored.Add(cell);
							else
							{
								explored.Add(cell);
								visible |= player.Shroud.IsVisible(cell);
							}
						}
					}

					var key = (sx, sy);
					if (visible)
						sectorLastSeen[key] = world.WorldTick;

					var total = explored.Count + unexplored.Count;
					if (total == 0)
						continue;

					List<CPos> pool;
					List<CPos> into;
					if (unexplored.Count * 2 > total)
						(pool, into) = (unexplored, unexploredSectors);
					else if (!visible && world.WorldTick - sectorLastSeen.GetValueOrDefault(key) > StaleTicks)
						(pool, into) = (explored, staleSectors);
					else
						continue;

					// Closest-to-center first; a few path checks per sector are enough.
					var reachable = pool.OrderBy(c => (c - center).LengthSquared).ThenBy(c => c.X).ThenBy(c => c.Y)
						.Take(8).Where(Reachable).Select(c => (CPos?)c).FirstOrDefault();
					if (reachable != null)
						into.Add(reachable.Value);
				}
			}

			var home = player.HomeLocation;
			IEnumerable<CPos> Nearest(List<CPos> cells) => cells.OrderBy(c => (c - home).LengthSquared).ThenBy(c => c.X).ThenBy(c => c.Y);
			return Nearest(unexploredSectors).Select(c => (c, "unexplored"))
				.Concat(Nearest(staleSectors).Select(c => (c, "stale")))
				.Take(MaxSectorTargets)
				.ToList();
		}
	}
}
