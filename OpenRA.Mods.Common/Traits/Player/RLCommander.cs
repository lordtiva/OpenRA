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
	/// v3: runs the rl-agent player as a modular bot (the normal AI's modules, enabled for
	/// rl-agent in rules) whose strategic decisions come from the agent.
	/// Brain "external": BUILD / TRAIN / ARMY requests go to the agent through the bridge.
	/// Brain "builtin": the modules' own heuristics decide and are reported as teacher decisions.
	/// Owned and ticked by <see cref="ExternalBotBridge"/> on the world thread.
	/// </summary>
	sealed class RLCommander
	{
		// Re-ask a queue this long after "wait" (or an unanswered request).
		const int WaitTicks = 100;
		const int ArmyInterval = 250;
		const int MaxTargets = 12;
		const int MaxSectorTargets = 4;
		const int StaleTicks = 2000;
		const int DedupCells = 6;
		const int ClearedRadius = 10;
		const int SectorCells = 16;
		static readonly HashSet<string> GroundTerrain = ["Clear", "Road", "Rough", "Ore", "Gems", "Beach", "Bridge"];
		static readonly WDist TargetRadius = WDist.FromCells(10);
		const string ArmyKey = "A";

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
		readonly Dictionary<string, uint> requestByKey = [];
		readonly Dictionary<string, int> waitUntil = [];
		readonly List<RLProto.DecisionRequest> newRequests = [];
		readonly List<RLProto.TeacherDecision> teacher = [];
		readonly Dictionary<uint, (CPos Cell, string Type)> seenEnemyBuildings = [];
		readonly Dictionary<(int X, int Y), int> sectorLastSeen = [];
		uint nextId = 1;
		int nextArmyTick = ArmyInterval;

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

		/// <summary>New requests since the last observation: an external advance stops on them.</summary>
		public bool HasNewRequests => External && newRequests.Count > 0;

		/// <summary>gRPC thread: answers sent with a FastAdvance (possibly none).</summary>
		public void Submit(IEnumerable<RLProto.Decision> decisions)
		{
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

		/// <summary>Move new requests and teacher decisions into an outgoing observation.</summary>
		public void FillObservation(RLProto.GameObservation obs)
		{
			obs.PendingDecisions.Add(newRequests);
			newRequests.Clear();
			obs.TeacherDecisions.Add(teacher);
			teacher.Clear();
		}

		void BeginStep()
		{
			while (inbox.TryDequeue(out var d))
			{
				if (!outstanding.Remove(d.RequestId, out var req))
					continue;

				if (req.Kind == RLProto.DecisionKind.DecideArmy)
					ApplyArmy(req, d.TargetIndex);
				else if (!StartProduction(requestQueues.GetValueOrDefault(d.RequestId), d.Choice))
					waitUntil[KeyOf(req)] = world.WorldTick + WaitTicks;
			}

			// Requests the agent did not answer count as "wait".
			foreach (var req in outstanding.Values)
				if (req.Kind != RLProto.DecisionKind.DecideArmy)
					waitUntil[KeyOf(req)] = world.WorldTick + WaitTicks;

			outstanding.Clear();
			requestQueues.Clear();
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
			return req.Kind == RLProto.DecisionKind.DecideArmy ? ArmyKey : $"{req.Kind}:{req.Queue}";
		}

		RLProto.DecisionRequest NewRequest(RLProto.DecisionKind kind, string queue)
		{
			return new RLProto.DecisionRequest { Id = nextId++, Kind = kind, Queue = queue ?? "", Tick = world.WorldTick };
		}

		void Ask(RLProto.DecisionRequest req)
		{
			outstanding[req.Id] = req;
			requestByKey[KeyOf(req)] = req.Id;
			newRequests.Add(req);
		}

		public ActorInfo DecideProduction(BotDecisionKind kind, ProductionQueue queue, Func<ActorInfo> nativeChoice)
		{
			var protoKind = kind == BotDecisionKind.Build ? RLProto.DecisionKind.DecideBuild : RLProto.DecisionKind.DecideTrain;
			var key = $"{protoKind}:{queue.Info.Type}";

			if (!External)
			{
				var native = nativeChoice();

				// Waits are reported at most once per WaitTicks, the cadence an external agent sees.
				if (native == null && waitUntil.TryGetValue(key, out var quiet) && world.WorldTick < quiet)
					return null;

				var req = NewRequest(protoKind, queue.Info.Type);
				req.Options.Add(queue.BuildableItems().Select(a => a.Name));
				teacher.Add(new RLProto.TeacherDecision
				{
					Request = req,
					Decision = new RLProto.Decision { RequestId = req.Id, Choice = native?.Name ?? "" },
				});

				if (native == null)
					waitUntil[key] = world.WorldTick + WaitTicks;

				return native;
			}

			if (requestByKey.ContainsKey(key) || (waitUntil.TryGetValue(key, out var until) && world.WorldTick < until))
				return null;

			var options = queue.BuildableItems().Select(a => a.Name).ToList();
			if (options.Count == 0)
				return null;

			var request = NewRequest(protoKind, queue.Info.Type);
			request.Options.Add(options);
			Ask(request);
			requestQueues[request.Id] = queue;
			return null;
		}

		SquadManagerBotModule Squads()
		{
			return player.PlayerActor.TraitsImplementing<SquadManagerBotModule>().FirstOrDefault(m => m.IsTraitEnabled());
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
			var req = NewRequest(RLProto.DecisionKind.DecideArmy, "");
			req.Targets.Add(BuildTargets());

			if (External)
			{
				if (!requestByKey.ContainsKey(ArmyKey))
					Ask(req);

				return;
			}

			var target = squads.RLArmyTarget();
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

		void ApplyArmy(RLProto.DecisionRequest req, int index)
		{
			var squads = Squads();
			if (squads == null)
				return;

			if (index < 0 || index >= req.Targets.Count)
				squads.RLHold(bot);
			else
			{
				var t = req.Targets[index];
				squads.RLAttack(bot, new CPos(t.CellX, t.CellY), TargetRadius);
			}
		}

		/// <summary>Enemy buildings the agent has seen and not yet seen destroyed (fog memory).</summary>
		void UpdateEnemyMemory()
		{
			foreach (var a in world.ActorsHavingTrait<Building>())
			{
				if (a.IsDead || !a.IsInWorld || player.RelationshipWith(a.Owner) != PlayerRelationship.Enemy
					|| a.Info.HasTraitInfo<LineBuildInfo>() || !a.CanBeViewedByPlayer(player))
					continue;

				seenEnemyBuildings[a.ActorID] = (a.Location, a.Info.Name);
			}

			var gone = new List<uint>();
			foreach (var (id, seen) in seenEnemyBuildings)
			{
				var a = world.GetActorById(id);
				if ((a == null || a.IsDead || !a.IsInWorld) && player.Shroud.IsVisible(seen.Cell))
					gone.Add(id);
			}

			foreach (var id in gone)
				seenEnemyBuildings.Remove(id);
		}

		/// <summary>
		/// Enemy start locations (until explored and found empty), remembered enemy buildings
		/// nearest our home, then the nearest mostly-unexplored map sectors (where an enemy
		/// expansion may hide). Everything here is the agent's own knowledge.
		/// </summary>
		List<RLProto.TargetCandidate> BuildTargets()
		{
			var targets = new List<RLProto.TargetCandidate>();
			bool TooClose(CPos c) => targets.Any(t => (new CPos(t.CellX, t.CellY) - c).LengthSquared <= DedupCells * DedupCells);
			bool KnownBuildingNear(CPos c) => seenEnemyBuildings.Values.Any(s => (s.Cell - c).LengthSquared <= ClearedRadius * ClearedRadius);

			foreach (var p in world.Players)
			{
				if (p.NonCombatant || player.RelationshipWith(p) != PlayerRelationship.Enemy || targets.Count >= MaxTargets)
					continue;

				var spawn = p.HomeLocation;
				if (!player.Shroud.IsExplored(spawn) || KnownBuildingNear(spawn))
					targets.Add(new RLProto.TargetCandidate { CellX = spawn.X, CellY = spawn.Y, Kind = "enemy_spawn" });
			}

			var home = player.HomeLocation;
			var buildingSlots = MaxTargets - MaxSectorTargets;
			foreach (var seen in seenEnemyBuildings.Values.OrderBy(s => (s.Cell - home).LengthSquared).ThenBy(s => s.Cell.X).ThenBy(s => s.Cell.Y))
			{
				if (targets.Count >= buildingSlots)
					break;

				if (!TooClose(seen.Cell))
					targets.Add(new RLProto.TargetCandidate { CellX = seen.Cell.X, CellY = seen.Cell.Y, Kind = "enemy_building", Type = seen.Type });
			}

			foreach (var (cell, kind) in SectorTargets())
			{
				if (targets.Count >= MaxTargets)
					break;

				if (!TooClose(cell))
					targets.Add(new RLProto.TargetCandidate { CellX = cell.X, CellY = cell.Y, Kind = kind });
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
