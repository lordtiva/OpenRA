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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits.BotModules.Squads;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Manages AI squads.")]
	public class SquadManagerBotModuleInfo : ConditionalTraitInfo
	{
		[ActorReference]
		[Desc("Actor types that are valid for naval squads.")]
		public readonly FrozenSet<string> NavalUnitsTypes = FrozenSet<string>.Empty;

		[ActorReference]
		[Desc("Actor types that are excluded from ground attacks.")]
		public readonly FrozenSet<string> AirUnitsTypes = FrozenSet<string>.Empty;

		[ActorReference]
		[Desc("Actor types that should generally be excluded from attack squads.")]
		public readonly FrozenSet<string> ExcludeFromSquadsTypes = FrozenSet<string>.Empty;

		[ActorReference]
		[Desc("Actor types that are considered construction yards (base builders).")]
		public readonly FrozenSet<string> ConstructionYardTypes = FrozenSet<string>.Empty;

		[ActorReference]
		[Desc("Enemy building types around which to scan for targets for naval squads.")]
		public readonly FrozenSet<string> NavalProductionTypes = FrozenSet<string>.Empty;

		[ActorReference]
		[Desc("Own actor types that are prioritized when defending.")]
		public readonly FrozenSet<string> ProtectionTypes = FrozenSet<string>.Empty;

		[Desc("Target types are used for identifying aircraft.")]
		public readonly BitSet<TargetableType> AircraftTargetType = new("Air");

		[Desc("Minimum number of units AI must have before attacking.")]
		public readonly int SquadSize = 8;

		[Desc("Random number of up to this many units is added to squad size when creating an attack squad.")]
		public readonly int SquadSizeRandomBonus = 30;

		[Desc("Delay (in ticks) between giving out orders to units.")]
		public readonly int AssignRolesInterval = 50;

		[Desc("Delay (in ticks) between attempting rush attacks.")]
		public readonly int RushInterval = 600;

		[Desc("Delay (in ticks) between updating squads.")]
		public readonly int AttackForceInterval = 75;

		[Desc("Minimum delay (in ticks) between creating squads.")]
		public readonly int MinimumAttackForceDelay = 0;

		[Desc("Radius in cells around enemy BaseBuilder (Construction Yard) where AI scans for targets to rush.")]
		public readonly int RushAttackScanRadius = 15;

		[Desc("Radius in cells around the base that should be scanned for units to be protected.")]
		public readonly int ProtectUnitScanRadius = 15;

		[Desc("Maximum distance in cells from center of the base when checking for MCV deployment location.",
			"Only applies if RestrictMCVDeploymentFallbackToBase is enabled and there's at least one construction yard.")]
		public readonly int MaxBaseRadius = 20;

		[Desc("Radius in cells that squads should scan for enemies around their position while idle.")]
		public readonly int IdleScanRadius = 10;

		[Desc("Radius in cells that squads should scan for danger around their position to make flee decisions.")]
		public readonly int DangerScanRadius = 10;

		[Desc("Radius in cells that attack squads should scan for enemies around their position when trying to attack.")]
		public readonly int AttackScanRadius = 12;

		[Desc("Radius in cells that protecting squads should scan for enemies around their position.")]
		public readonly int ProtectionScanRadius = 8;

		[Desc("Enemy target types to never target.")]
		public readonly BitSet<TargetableType> IgnoredEnemyTargetTypes = default;

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);

			if (DangerScanRadius <= 0)
				throw new YamlException("DangerScanRadius must be greater than zero.");
		}

		public override object Create(ActorInitializer init) { return new SquadManagerBotModule(init.Self, this); }
	}

	public class SquadManagerBotModule : ConditionalTrait<SquadManagerBotModuleInfo>,
		IBotEnabled, IBotTick, IBotRespondToAttack, IBotPositionsUpdated, IGameSaveTraitData, INotifyActorDisposing
	{
		public CPos GetRandomBaseCenter()
		{
			var randomConstructionYard = constructionYardBuildings.Actors
				.Where(a => a.Owner == Player)
				.RandomOrDefault(World.LocalRandom);

			return randomConstructionYard?.Location ?? initialBaseCenter;
		}

		const int MaxRespondToAttackCooldown = 30;

		public readonly World World;
		public readonly Player Player;

		readonly Predicate<Actor> unitCannotBeOrdered;

		// Units that the bot already knows about. Any unit not on this list needs to be given a role.
		readonly HashSet<Actor> activeUnits = [];

		// v4 tactical groups: independent fast scout/harass detachment kept separate from MAIN.
		static readonly FrozenSet<string> FastHarassTypes = new HashSet<string> { "jeep", "ftrk", "apc" }.ToFrozenSet();
		const int MaxHarassUnits = 4;
		readonly HashSet<Actor> rlHarassUnits = [];
		Squad rlHarassSquad;

		public List<Squad> Squads = [];
		readonly Stack<Squad> squadsPendingUpdate = [];
		readonly ActorIndex.NamesAndTrait<BuildingInfo> constructionYardBuildings;

		IBot bot;
		IBotPositionsUpdated[] notifyPositionsUpdated;
		IBotNotifyIdleBaseUnits[] notifyIdleBaseUnits;

		List<Actor> unitsHangingAroundTheBase = [];
		CPos initialBaseCenter;

		Actor protectFrom;

		int rushTicks;
		int assignRolesTicks;
		int attackForceTicks;
		int minAttackForceDelayTicks;
		int respondToAttackCooldown = MaxRespondToAttackCooldown; // prevent too many responses to the same wave of attacks

		public SquadManagerBotModule(Actor self, SquadManagerBotModuleInfo info)
			: base(info)
		{
			World = self.World;
			Player = self.Owner;

			unitCannotBeOrdered = a => a == null || a.Owner != Player || a.IsDead || !a.IsInWorld;
			constructionYardBuildings = new ActorIndex.NamesAndTrait<BuildingInfo>(World, info.ConstructionYardTypes);
		}

		// Use for proactive targeting.
		public bool IsPreferredEnemyUnit(Actor a)
		{
			if (a == null || a.IsDead || Player.RelationshipWith(a.Owner) != PlayerRelationship.Enemy || a.Info.HasTraitInfo<HuskInfo>())
				return false;

			var targetTypes = a.GetEnabledTargetTypes();
			if (targetTypes.IsEmpty || targetTypes.Overlaps(Info.IgnoredEnemyTargetTypes))
				return false;

			return IsNotHiddenUnit(a);
		}

		bool IsNotHiddenUnit(Actor a)
		{
			var hasModifier = false;
			var visModifiers = a.TraitsImplementing<IVisibilityModifier>();
			foreach (var v in visModifiers)
			{
				if (v.IsVisible(a, Player))
					return true;

				hasModifier = true;
			}

			return !hasModifier;
		}

		protected override void Created(Actor self)
		{
			notifyPositionsUpdated = self.Owner.PlayerActor.TraitsImplementing<IBotPositionsUpdated>().ToArray();
			notifyIdleBaseUnits = self.Owner.PlayerActor.TraitsImplementing<IBotNotifyIdleBaseUnits>().ToArray();
		}

		protected override void TraitEnabled(Actor self)
		{
			// Avoid all AIs trying to rush in the same tick, randomize their initial rush a little.
			var smallFractionOfRushInterval = Info.RushInterval / 20;
			rushTicks = World.LocalRandom.Next(Info.RushInterval - smallFractionOfRushInterval, Info.RushInterval + smallFractionOfRushInterval);

			// Avoid all AIs reevaluating assignments on the same tick, randomize their initial evaluation delay.
			assignRolesTicks = World.LocalRandom.Next(0, Info.AssignRolesInterval);
			attackForceTicks = World.LocalRandom.Next(0, Info.AttackForceInterval);
			minAttackForceDelayTicks = World.LocalRandom.Next(0, Info.MinimumAttackForceDelay);
		}

		void IBotEnabled.BotEnabled(IBot bot)
		{
			this.bot = bot;
		}

		void IBotTick.BotTick(IBot bot)
		{
			AssignRolesToIdleUnits(bot);
		}

		internal static Actor ClosestTo(IEnumerable<Actor> ownActors, Actor targetActor)
		{
			// Return actors that can get within weapons range of the target.
			// First, let's determine the max weapons range for each of the actors.
			var target = Target.FromActor(targetActor);
			var ownActorsAndTheirAttackRanges = ownActors
				.Select(a => (Actor: a, AttackBases: a.TraitsImplementing<AttackBase>().Where(Exts.IsTraitEnabled)
					.Where(ab => ab.HasAnyValidWeapons(target)).ToList()))
				.Where(x => x.AttackBases.Count > 0)
				.Select(x => (x.Actor, Range: x.AttackBases.Max(ab => ab.GetMaximumRangeVersusTarget(target))))
				.ToDictionary(x => x.Actor, x => x.Range);

			// Now determine if each actor can either path directly to the target,
			// or if it can path to a nearby location at the edge of its weapon range to the target
			// A thorough check would check each position within the circle, but for performance
			// we'll only check 8 positions around the edge of the circle.
			// We need to account for the weapons range here to account for units such as boats.
			// They can't path directly to a land target,
			// but might be able to get close enough to shore to attack the target from range.
			return ownActorsAndTheirAttackRanges.Keys
				.ClosestToWithPathToAny(targetActor.World, a =>
				{
					var range = ownActorsAndTheirAttackRanges[a].Length;
					var rangeDiag = Exts.MultiplyBySqrtTwoOverTwo(range);
					return
					[
						targetActor.CenterPosition,
						targetActor.CenterPosition + new WVec(range, 0, 0),
						targetActor.CenterPosition + new WVec(-range, 0, 0),
						targetActor.CenterPosition + new WVec(0, range, 0),
						targetActor.CenterPosition + new WVec(0, -range, 0),
						targetActor.CenterPosition + new WVec(rangeDiag, rangeDiag, 0),
						targetActor.CenterPosition + new WVec(-rangeDiag, rangeDiag, 0),
						targetActor.CenterPosition + new WVec(-rangeDiag, -rangeDiag, 0),
						targetActor.CenterPosition + new WVec(rangeDiag, -rangeDiag, 0),
					];
				});
		}

		internal IEnumerable<(Actor Actor, WVec Offset)> FindEnemies(IEnumerable<Actor> actors, Actor sourceActor)
		{
			// Check units are in fact enemies and not hidden.
			// Then check which are in weapons range of the source.
			var activeAttackBases = sourceActor.TraitsImplementing<AttackBase>().Where(Exts.IsTraitEnabled).ToArray();
			var enemiesAndSourceAttackRanges = actors
				.Where(IsPreferredEnemyUnit)
				.Select(a => (Actor: a, AttackBases: activeAttackBases.Where(ab => ab.HasAnyValidWeapons(Target.FromActor(a))).ToList()))
				.Where(x => x.AttackBases.Count > 0)
				.Select(x => (x.Actor, Range: x.AttackBases.Max(ab => ab.GetMaximumRangeVersusTarget(Target.FromActor(x.Actor)))))
				.ToDictionary(x => x.Actor, x => x.Range);

			// Now determine if the source actor can path directly to the target,
			// or if it can path to a nearby location at the edge of its weapon range to the target
			// A thorough check would check each position within the circle, but for performance
			// we'll only check 8 positions around the edge of the circle.
			// We need to account for the weapons range here to account for units such as boats.
			// They can't path directly to a land target,
			// but might be able to get close enough to shore to attack the target from range.
			return enemiesAndSourceAttackRanges.Keys
				.WithPathFrom(sourceActor, a =>
				{
					var range = enemiesAndSourceAttackRanges[a].Length;
					var rangeDiag = Exts.MultiplyBySqrtTwoOverTwo(range);
					return
					[
						WVec.Zero,
						new WVec(range, 0, 0),
						new WVec(-range, 0, 0),
						new WVec(0, range, 0),
						new WVec(0, -range, 0),
						new WVec(rangeDiag, rangeDiag, 0),
						new WVec(-rangeDiag, rangeDiag, 0),
						new WVec(-rangeDiag, -rangeDiag, 0),
						new WVec(rangeDiag, -rangeDiag, 0),
					];
				})
				.Select(x => (x.Actor, x.ReachableOffsets.MinBy(o => o.LengthSquared)));
		}

		internal (Actor Actor, WVec Offset) FindClosestEnemy(Actor sourceActor)
		{
			return FindClosestEnemy(World.Actors, sourceActor);
		}

		internal (Actor Actor, WVec Offset) FindClosestEnemy(Actor sourceActor, WDist radius)
		{
			return FindClosestEnemy(World.FindActorsInCircle(sourceActor.CenterPosition, radius), sourceActor);
		}

		(Actor Actor, WVec Offset) FindClosestEnemy(IEnumerable<Actor> actors, Actor sourceActor)
		{
			return WorldUtils.ClosestToIgnoringPath(FindEnemies(actors, sourceActor), x => x.Actor, sourceActor);
		}

		void CleanSquads()
		{
			foreach (var s in Squads)
				s.Units.RemoveWhere(unitCannotBeOrdered);
			Squads.RemoveAll(s => !s.IsValid);
			rlHarassUnits.RemoveWhere(unitCannotBeOrdered);
			if (rlHarassSquad != null && (!rlHarassSquad.IsValid || !Squads.Contains(rlHarassSquad)))
				rlHarassSquad = null;
		}

		// HACK: Use of this function requires that there is one squad of this type.
		Squad GetSquadOfType(SquadType type)
		{
			return Squads.FirstOrDefault(s => s.Type == type);
		}

		Squad RegisterNewSquad(IBot bot, SquadType type, (Actor Actor, WVec Offset) target = default)
		{
			var ret = new Squad(bot, this, type, target);
			Squads.Add(ret);
			return ret;
		}

		internal void UnregisterSquad(Squad squad)
		{
			if (ReferenceEquals(squad, rlHarassSquad))
			{
				rlHarassUnits.RemoveWhere(unitCannotBeOrdered);
				activeUnits.UnionWith(rlHarassUnits);
				squad.Units.Clear();
				rlHarassSquad = null;
				return;
			}

			activeUnits.ExceptWith(squad.Units);
			squad.Units.Clear();

			// CleanSquads will remove the squad from the Squads list.
			// We can't do that here as this is designed to be called from within Squad.Update
			// and thus would mutate the Squads list we are iterating over.
		}

		void AssignRolesToIdleUnits(IBot bot)
		{
			CleanSquads();

			activeUnits.RemoveWhere(unitCannotBeOrdered);
			unitsHangingAroundTheBase.RemoveAll(unitCannotBeOrdered);
			foreach (var n in notifyIdleBaseUnits)
				n.UpdatedIdleBaseUnits(unitsHangingAroundTheBase);

			// An external agent decides when and where the army attacks (RLAttack / RLHold).
			var external = bot is IBotDecisionBroker broker && broker.ExternalControl;

			if (--rushTicks <= 0)
			{
				rushTicks = Info.RushInterval;
				if (!external)
					TryToRushAttack(bot);
			}

			if (--attackForceTicks <= 0)
			{
				attackForceTicks = Info.AttackForceInterval;
				foreach (var s in Squads)
					squadsPendingUpdate.Push(s);
			}

			// PERF: Spread out squad updates across multiple ticks.
			var updateCount = Exts.IntegerDivisionRoundingAwayFromZero(squadsPendingUpdate.Count, attackForceTicks);
			for (var i = 0; i < updateCount; i++)
			{
				var squadPendingUpdate = squadsPendingUpdate.Pop();
				if (squadPendingUpdate.IsValid)
					squadPendingUpdate.Update();
			}

			if (--assignRolesTicks <= 0)
			{
				assignRolesTicks = Info.AssignRolesInterval;
				FindNewUnits(bot);
			}

			if (--minAttackForceDelayTicks <= 0)
			{
				minAttackForceDelayTicks = Info.MinimumAttackForceDelay;
				if (!external)
					CreateAttackForce(bot);
			}

			if (respondToAttackCooldown-- == MaxRespondToAttackCooldown)
				ProtectOwn(bot, protectFrom);
		}

		void FindNewUnits(IBot bot)
		{
			var newUnits = World.ActorsHavingTrait<IPositionable>()
				.Where(a => a.Owner == Player &&
					!Info.ExcludeFromSquadsTypes.Contains(a.Info.Name) &&
					!activeUnits.Contains(a));

			foreach (var a in newUnits)
			{
				if (Info.AirUnitsTypes.Contains(a.Info.Name))
				{
					var air = GetSquadOfType(SquadType.Air);
					air ??= RegisterNewSquad(bot, SquadType.Air);

					air.Units.Add(a);
				}
				else if (Info.NavalUnitsTypes.Contains(a.Info.Name))
				{
					var ships = GetSquadOfType(SquadType.Naval);
					ships ??= RegisterNewSquad(bot, SquadType.Naval);

					ships.Units.Add(a);
				}
				else
					unitsHangingAroundTheBase.Add(a);

				activeUnits.Add(a);
			}

			// Notifying here rather than inside the loop, should be fine and saves a bunch of notification calls
			foreach (var n in notifyIdleBaseUnits)
				n.UpdatedIdleBaseUnits(unitsHangingAroundTheBase);
		}

		void CreateAttackForce(IBot bot)
		{
			// Create an attack force when we have enough units around our base.
			// (don't bother leaving any behind for defense)
			var randomizedSquadSize = Info.SquadSize + World.LocalRandom.Next(Info.SquadSizeRandomBonus);

			if (unitsHangingAroundTheBase.Count >= randomizedSquadSize)
			{
				var attackForce = RegisterNewSquad(bot, SquadType.Assault);

				attackForce.Units.UnionWith(unitsHangingAroundTheBase);

				unitsHangingAroundTheBase.Clear();
				foreach (var n in notifyIdleBaseUnits)
					n.UpdatedIdleBaseUnits(unitsHangingAroundTheBase);
			}
		}

		void TryToRushAttack(IBot bot)
		{
			var groundTroopNum = unitsHangingAroundTheBase.Count;

			foreach (var s in Squads)
			{
				if (s.IsValid && s.Type != SquadType.Air && s.Type != SquadType.Naval)
					groundTroopNum += s.Units.Count;
			}

			if (groundTroopNum < Info.SquadSize)
				return;

			var randomAttackableUnit = unitsHangingAroundTheBase.Where(a => a.Info.HasTraitInfo<AttackBaseInfo>()).RandomOrDefault(World.LocalRandom);

			if (randomAttackableUnit == null)
				return;

			var allEnemyBaseBuilder = FindEnemies(
				constructionYardBuildings.Actors,
				randomAttackableUnit)
				.ToList();

			if (allEnemyBaseBuilder.Count == 0)
				return;

			foreach (var enemyBaseBuilder in allEnemyBaseBuilder)
			{
				// Don't rush enemy aircraft!
				var enemies = FindEnemies(
					World.FindActorsInCircle(enemyBaseBuilder.Actor.CenterPosition, WDist.FromCells(Info.RushAttackScanRadius))
						.Where(unit =>
							unit.Info.HasTraitInfo<AttackBaseInfo>()
							&& !Info.AirUnitsTypes.Contains(unit.Info.Name)
							&& !Info.NavalUnitsTypes.Contains(unit.Info.Name)),
					randomAttackableUnit)
					.ToList();

				if (AttackOrFleeFuzzy.Rush.CanAttack(unitsHangingAroundTheBase, enemies.ConvertAll(x => x.Actor)))
				{
					var target = enemies.Count > 0 ? enemies.Random(World.LocalRandom) : enemyBaseBuilder;
					foreach (var s in Squads)
					{
						if (s.IsValid)
							s.SetActorToTarget(target);
					}

					var rush = GetSquadOfType(SquadType.Rush);
					rush ??= RegisterNewSquad(bot, SquadType.Rush, target);

					rush.Units.UnionWith(unitsHangingAroundTheBase);
					unitsHangingAroundTheBase.Clear();
					foreach (var n in notifyIdleBaseUnits)
						n.UpdatedIdleBaseUnits(unitsHangingAroundTheBase);

					return;
				}
			}
		}

		void ProtectOwn(IBot bot, Actor attacker)
		{
			if (!IsPreferredEnemyUnit(attacker))
				return;

			foreach (var s in Squads)
			{
				if (s.IsValid && FindEnemies([attacker], s.Units.First()).Any()
					&& (s.Units.First().Location - attacker.Location).LengthSquared <= Info.ProtectUnitScanRadius * Info.ProtectUnitScanRadius)
					s.SetActorToTarget((attacker, WVec.Zero));
			}

			var protectSq = GetSquadOfType(SquadType.Protection);
			protectSq ??= RegisterNewSquad(bot, SquadType.Protection, (attacker, WVec.Zero));

			var unusedUnits = new List<Actor>();
			foreach (var a in unitsHangingAroundTheBase)
			{
				if (a.Info.HasTraitInfo<AircraftInfo>() && !Info.AirUnitsTypes.Contains(a.Info.Name))
				{
					protectSq.Units.Add(a);
					continue;
				}

				var mobile = a.TraitOrDefault<Mobile>();
				if (mobile == null)
				{
					unusedUnits.Add(a);
					continue;
				}

				if (FindEnemies([attacker], a).Any())
					protectSq.Units.Add(a);
				else
					unusedUnits.Add(a);
			}

			unitsHangingAroundTheBase = unusedUnits;
			foreach (var n in notifyIdleBaseUnits)
				n.UpdatedIdleBaseUnits(unitsHangingAroundTheBase);

			if (protectSq.IsValid && !protectSq.IsTargetValid(protectSq.CenterUnit()))
				protectSq.SetActorToTarget((attacker, WVec.Zero));
		}

		// RL commander (IBotDecisionBroker): v4 supports three tactical groups:
		//   - MAIN: main ground battleforce (normal micro or fearless all_in push)
		//   - HARASS: up to MaxHarassUnits fast scouts/raiders excluded from MAIN
		//   - AIR: helicopters / fixed-wing aircraft (SquadType.Air)

		internal IEnumerable<uint> RLHarassActorIds()
		{
			CleanSquads();
			return rlHarassUnits.Select(a => a.ActorID).OrderBy(id => id);
		}

		int CountOrderableGroundUnits()
		{
			var seen = new HashSet<Actor>();
			foreach (var a in unitsHangingAroundTheBase)
				if (!unitCannotBeOrdered(a) && !Info.AirUnitsTypes.Contains(a.Info.Name) && !Info.NavalUnitsTypes.Contains(a.Info.Name))
					seen.Add(a);
			foreach (var a in rlHarassUnits)
				if (!unitCannotBeOrdered(a))
					seen.Add(a);
			foreach (var s in Squads)
			{
				if (s.Type == SquadType.Air || s.Type == SquadType.Naval)
					continue;
				foreach (var a in s.Units)
					if (!unitCannotBeOrdered(a))
						seen.Add(a);
			}

			return seen.Count;
		}

		internal bool RLCanHarass()
		{
			CleanSquads();
			if (rlHarassUnits.Count > 0)
				return true;

			if (unitsHangingAroundTheBase.Any(a => !unitCannotBeOrdered(a) && FastHarassTypes.Contains(a.Info.Name)))
				return true;

			foreach (var s in Squads)
			{
				if ((s.Type == SquadType.Assault || s.Type == SquadType.Rush)
					&& s.Units.Count > 4
					&& s.Units.Any(a => !unitCannotBeOrdered(a) && FastHarassTypes.Contains(a.Info.Name)))
					return true;
			}

			return CountOrderableGroundUnits() >= 5
				&& unitsHangingAroundTheBase.Any(a => !unitCannotBeOrdered(a) && a.Info.Name == "e1");
		}

		Squad SyncAirSquad(IBot bot)
		{
			CleanSquads();
			var airActors = World.ActorsHavingTrait<IPositionable>()
				.Where(a => a.Owner == Player && !unitCannotBeOrdered(a) && Info.AirUnitsTypes.Contains(a.Info.Name))
				.OrderBy(a => a.ActorID)
				.ToList();
			if (airActors.Count == 0)
				return null;

			var air = GetSquadOfType(SquadType.Air);
			air ??= RegisterNewSquad(bot, SquadType.Air);
			foreach (var a in airActors)
			{
				air.Units.Add(a);
				activeUnits.Add(a);
			}

			return air.IsValid ? air : null;
		}

		internal bool RLHasAir()
		{
			CleanSquads();
			return World.ActorsHavingTrait<IPositionable>()
				.Any(a => a.Owner == Player && !unitCannotBeOrdered(a) && Info.AirUnitsTypes.Contains(a.Info.Name));
		}

		/// <summary>Enemy target of the specified tactical group, or null if idle/home.</summary>
		internal Actor RLArmyTarget(string group = "MAIN")
		{
			CleanSquads();
			if (string.Equals(group, "HARASS", StringComparison.OrdinalIgnoreCase))
				return rlHarassSquad != null && rlHarassSquad.IsValid && rlHarassSquad.TargetActor != null && !rlHarassSquad.TargetActor.IsDead
					? rlHarassSquad.TargetActor : null;

			if (string.Equals(group, "AIR", StringComparison.OrdinalIgnoreCase))
			{
				var air = GetSquadOfType(SquadType.Air);
				return air != null && air.IsValid && air.TargetActor != null && !air.TargetActor.IsDead
					? air.TargetActor : null;
			}

			foreach (var s in Squads)
				if (s.IsValid && !ReferenceEquals(s, rlHarassSquad)
					&& (s.Type == SquadType.Assault || s.Type == SquadType.Rush)
					&& s.TargetActor != null && !s.TargetActor.IsDead)
					return s.TargetActor;

			return null;
		}

		HashSet<Actor> TakeMainArmy()
		{
			CleanSquads();
			var army = new HashSet<Actor>(unitsHangingAroundTheBase.Where(a => !rlHarassUnits.Contains(a)));
			foreach (var s in Squads)
				if ((s.Type == SquadType.Assault || s.Type == SquadType.Rush) && !ReferenceEquals(s, rlHarassSquad))
					army.UnionWith(s.Units.Where(a => !rlHarassUnits.Contains(a)));

			Squads.RemoveAll(s => (s.Type == SquadType.Assault || s.Type == SquadType.Rush) && !ReferenceEquals(s, rlHarassSquad));
			army.RemoveWhere(unitCannotBeOrdered);
			unitsHangingAroundTheBase.RemoveAll(a => !rlHarassUnits.Contains(a));
			return army;
		}

		void RecruitHarassUnits()
		{
			CleanSquads();
			unitsHangingAroundTheBase.RemoveAll(rlHarassUnits.Contains);

			// Upgrade rifle scouts to fast vehicles once vehicles are available at home.
			var idleFast = unitsHangingAroundTheBase
				.Where(a => !unitCannotBeOrdered(a) && FastHarassTypes.Contains(a.Info.Name))
				.OrderBy(a => a.ActorID)
				.ToList();
			if (idleFast.Count > 0 && rlHarassUnits.Any(a => !FastHarassTypes.Contains(a.Info.Name)))
			{
				var demoted = rlHarassUnits.Where(a => !FastHarassTypes.Contains(a.Info.Name)).OrderBy(a => a.ActorID).ToList();
				foreach (var d in demoted)
				{
					rlHarassUnits.Remove(d);
					rlHarassSquad?.Units.Remove(d);
					unitsHangingAroundTheBase.Add(d);
				}
			}

			foreach (var fast in idleFast)
			{
				if (rlHarassUnits.Count >= MaxHarassUnits)
					break;
				unitsHangingAroundTheBase.Remove(fast);
				rlHarassUnits.Add(fast);
				activeUnits.Add(fast);
			}

			// Early-game fallback: up to 2 scouts (dogs or rifles) when no fast vehicles exist yet.
			if (rlHarassUnits.Count == 0 && CountOrderableGroundUnits() >= 5)
			{
				var scouts = unitsHangingAroundTheBase
					.Where(a => !unitCannotBeOrdered(a) && (a.Info.Name == "e1" || a.Info.Name == "dog"))
					.OrderBy(a => a.ActorID)
					.Take(2)
					.ToList();
				foreach (var sc in scouts)
				{
					unitsHangingAroundTheBase.Remove(sc);
					rlHarassUnits.Add(sc);
					activeUnits.Add(sc);
				}
			}

			foreach (var n in notifyIdleBaseUnits)
				n.UpdatedIdleBaseUnits(unitsHangingAroundTheBase);
		}

		void ReturnToBasePool(IEnumerable<Actor> units)
		{
			unitsHangingAroundTheBase.AddRange(units);
			foreach (var n in notifyIdleBaseUnits)
				n.UpdatedIdleBaseUnits(unitsHangingAroundTheBase);
		}

		internal void RLAttackMain(IBot bot, CPos cell, WDist radius, bool fearless = false)
		{
			CleanSquads();
			var orderable = unitsHangingAroundTheBase.Where(a => !rlHarassUnits.Contains(a) && !unitCannotBeOrdered(a)).ToList();
			var homeUnits = !fearless && orderable.Any(a => !a.Info.HasTraitInfo<TakeCoverInfo>())
				? orderable.Where(a => !a.Info.HasTraitInfo<TakeCoverInfo>()).ToList()
				: orderable;
			var existingSquads = Squads.Where(s => s.IsValid && (s.Type == SquadType.Assault || s.Type == SquadType.Rush) && !ReferenceEquals(s, rlHarassSquad)).ToList();

			if (homeUnits.Count == 0 && existingSquads.Count == 0)
				return;

			var pos = World.Map.CenterOfCell(cell);
			var visibleCandidates = World.FindActorsInCircle(pos, radius)
				.Where(a => IsPreferredEnemyUnit(a) && (a.CanBeViewedByPlayer(Player)
					|| (a.Info.HasTraitInfo<BuildingInfo>() && Player.Shroud.IsExplored(a.Location))))
				.ToList();

			var enemyActor = visibleCandidates.MinByOrDefault(a => (a.Location - cell).LengthSquared);
			var target = enemyActor != null ? (enemyActor, WVec.Zero) : default;
			var orderTarget = enemyActor != null ? Target.FromActor(enemyActor) : Target.FromCell(World, cell);
			var orderName = enemyActor != null ? "Attack" : "AssaultMove";

			// Keep existing assault squads active on the front line; retarget them without dismantling or freezing them.
			foreach (var sq in existingSquads)
			{
				sq.Fearless = true;
				if (sq.Units.Count > 0)
				{
					if (enemyActor != null)
						sq.SetActorToTarget(target);
					bot.QueueOrder(new Order(orderName, null, orderTarget, false, groupedActors: sq.Units.ToArray()));
				}
			}

			// Deploy any idle home units into the attack wave.
			// If an attack is already underway, only release reinforcements once a coherent detachment (>= 4 units) has assembled at home,
			// to prevent feeding single units piecemeal into enemy fire.
			if (homeUnits.Count >= (existingSquads.Count > 0 ? 4 : 1))
			{
				unitsHangingAroundTheBase.RemoveAll(homeUnits.Contains);
				var squad = RegisterNewSquad(bot, SquadType.Assault, target);
				squad.Fearless = true;
				squad.Units.UnionWith(homeUnits);
				bot.QueueOrder(new Order(orderName, null, orderTarget, false, groupedActors: homeUnits.ToArray()));
			}

			foreach (var n in notifyIdleBaseUnits)
				n.UpdatedIdleBaseUnits(unitsHangingAroundTheBase);
		}

		internal void RLAttack(IBot bot, CPos cell, WDist radius) => RLAttackMain(bot, cell, radius, false);

		internal void RLAttackHarass(IBot bot, CPos cell, WDist radius)
		{
			RecruitHarassUnits();
			if (rlHarassUnits.Count == 0)
				return;

			if (rlHarassSquad != null)
			{
				Squads.Remove(rlHarassSquad);
				rlHarassSquad = null;
			}

			var units = rlHarassUnits.OrderBy(a => a.ActorID).ToArray();
			var pos = World.Map.CenterOfCell(cell);
			var visibleCandidates = World.FindActorsInCircle(pos, radius)
				.Where(a => IsPreferredEnemyUnit(a) && (a.CanBeViewedByPlayer(Player)
					|| (a.Info.HasTraitInfo<BuildingInfo>() && Player.Shroud.IsExplored(a.Location))))
				.ToList();
			var enemyActor = visibleCandidates.MinByOrDefault(a => (a.Location - cell).LengthSquared);
			var target = enemyActor != null ? (enemyActor, WVec.Zero) : default;
			var orderTarget = enemyActor != null ? Target.FromActor(enemyActor) : Target.FromCell(World, cell);

			rlHarassSquad = RegisterNewSquad(bot, SquadType.Rush, target);
			rlHarassSquad.Fearless = true;
			rlHarassSquad.Units.UnionWith(units);
			activeUnits.UnionWith(rlHarassUnits);
			var orderName = enemyActor != null ? "Attack" : "AssaultMove";
			bot.QueueOrder(new Order(orderName, null, orderTarget, false, groupedActors: units));
		}

		internal void RLAttackAir(IBot bot, CPos cell, WDist radius)
		{
			var air = SyncAirSquad(bot);
			if (air == null || !air.IsValid)
				return;

			var units = air.Units.OrderBy(a => a.ActorID).ToArray();
			var pos = World.Map.CenterOfCell(cell);
			var source = units.MinBy(a => (a.CenterPosition - pos).LengthSquared);
			var candidates = World.FindActorsInCircle(pos, radius)
				.Where(a => IsPreferredEnemyUnit(a) && (a.CanBeViewedByPlayer(Player)
					|| (a.Info.HasTraitInfo<BuildingInfo>() && Player.Shroud.IsExplored(a.Location))));
			var target = FindEnemies(candidates, source)
				.MinByOrDefault(x => (x.Actor.CenterPosition - pos).LengthSquared);

			if (target.Actor != null)
			{
				air.SetActorToTarget(target);
				air.FuzzyStateMachine.ChangeState(air, new AirAttackState());
				air.Update();
				return;
			}

			// Aerial reconnaissance over fogged/unexplored sector
			air.SetActorToTarget(default);
			air.FuzzyStateMachine.ChangeState(air, new AirIdleState());
			foreach (var a in units)
			{
				var ammoPools = a.TraitsImplementing<AmmoPool>().ToArray();
				if (ammoPools.Length > 0 && ammoPools.All(ap => ap.CurrentAmmoCount == 0) && a.TraitOrDefault<Rearmable>() != null)
					bot.QueueOrder(new Order("ReturnToBase", a, false));
				else
					bot.QueueOrder(new Order("Move", a, Target.FromCell(World, cell), false));
			}
		}

		internal void RLHoldGroup(IBot bot, string group)
		{
			var home = initialBaseCenter != CPos.Zero ? initialBaseCenter : Player.HomeLocation;

			if (string.Equals(group, "HARASS", StringComparison.OrdinalIgnoreCase))
			{
				CleanSquads();
				if (rlHarassSquad != null)
				{
					Squads.Remove(rlHarassSquad);
					rlHarassSquad = null;
				}

				if (rlHarassUnits.Count == 0)
					return;

				var units = rlHarassUnits.OrderBy(a => a.ActorID).ToArray();
				rlHarassUnits.Clear();
				bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(World, home), false, groupedActors: units));
				ReturnToBasePool(units);
				return;
			}

			if (string.Equals(group, "AIR", StringComparison.OrdinalIgnoreCase))
			{
				var air = GetSquadOfType(SquadType.Air);
				if (air == null || !air.IsValid)
					return;

				air.SetActorToTarget(default);
				air.FuzzyStateMachine.ChangeState(air, new AirIdleState());
				foreach (var a in air.Units.OrderBy(u => u.ActorID))
				{
					if (a.TraitOrDefault<Rearmable>() != null)
						bot.QueueOrder(new Order("ReturnToBase", a, false));
					else
						bot.QueueOrder(new Order("Move", a, Target.FromCell(World, home), false));
				}

				return;
			}

			// MAIN group
			if (!Squads.Any(s => s.IsValid && !ReferenceEquals(s, rlHarassSquad) && (s.Type == SquadType.Assault || s.Type == SquadType.Rush)))
				return;

			var army = TakeMainArmy();
			if (army.Count == 0)
				return;

			bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(World, home), false, groupedActors: army.ToArray()));
			ReturnToBasePool(army);
		}

		/// <summary>Call off any main attack wave and bring the army back to the base center.</summary>
		internal void RLHold(IBot bot) => RLHoldGroup(bot, "MAIN");

		void IBotPositionsUpdated.UpdatedBaseCenter(CPos newLocation)
		{
			initialBaseCenter = newLocation;
		}

		void IBotPositionsUpdated.UpdatedDefenseCenter(CPos newLocation) { }

		void IBotRespondToAttack.RespondToAttack(IBot bot, Actor self, AttackInfo e)
		{
			if (respondToAttackCooldown > 0 || !Info.ProtectionTypes.Contains(self.Info.Name) || !IsPreferredEnemyUnit(e.Attacker))
				return;

			respondToAttackCooldown = MaxRespondToAttackCooldown;
			foreach (var n in notifyPositionsUpdated)
				n.UpdatedDefenseCenter(e.Attacker.Location);

			protectFrom = e.Attacker;
		}

		List<MiniYamlNode> IGameSaveTraitData.IssueTraitData(Actor self)
		{
			if (IsTraitDisabled)
				return null;

			return
			[
				new("Squads", "", Squads.ConvertAll(s => new MiniYamlNode("Squad", s.Serialize()))),
				new("InitialBaseCenter", FieldSaver.FormatValue(initialBaseCenter)),
				new("UnitsHangingAroundTheBase", FieldSaver.FormatValue(unitsHangingAroundTheBase
					.Where(a => !unitCannotBeOrdered(a))
					.Select(a => a.ActorID)
					.ToArray())),
				new("ActiveUnits", FieldSaver.FormatValue(activeUnits
					.Where(a => !unitCannotBeOrdered(a))
					.Select(a => a.ActorID)
					.ToArray())),
				new("RushTicks", FieldSaver.FormatValue(rushTicks)),
				new("AssignRolesTicks", FieldSaver.FormatValue(assignRolesTicks)),
				new("AttackForceTicks", FieldSaver.FormatValue(attackForceTicks)),
				new("MinAttackForceDelayTicks", FieldSaver.FormatValue(minAttackForceDelayTicks)),
			];
		}

		void IGameSaveTraitData.ResolveTraitData(Actor self, MiniYaml data)
		{
			if (self.World.IsReplay)
				return;

			var nodes = data.ToDictionary();

			if (nodes.TryGetValue("InitialBaseCenter", out var initialBaseCenterNode))
				initialBaseCenter = FieldLoader.GetValue<CPos>("InitialBaseCenter", initialBaseCenterNode.Value);

			if (nodes.TryGetValue("UnitsHangingAroundTheBase", out var unitsHangingAroundTheBaseNode))
			{
				unitsHangingAroundTheBase.Clear();
				unitsHangingAroundTheBase.AddRange(FieldLoader.GetValue<uint[]>("UnitsHangingAroundTheBase", unitsHangingAroundTheBaseNode.Value)
					.Select(self.World.GetActorById).Where(a => a != null));
			}

			if (nodes.TryGetValue("ActiveUnits", out var activeUnitsNode))
			{
				activeUnits.Clear();
				activeUnits.UnionWith(FieldLoader.GetValue<uint[]>("ActiveUnits", activeUnitsNode.Value)
					.Select(self.World.GetActorById).Where(a => a != null));
			}

			if (nodes.TryGetValue("RushTicks", out var rushTicksNode))
				rushTicks = FieldLoader.GetValue<int>("RushTicks", rushTicksNode.Value);

			if (nodes.TryGetValue("AssignRolesTicks", out var assignRolesTicksNode))
				assignRolesTicks = FieldLoader.GetValue<int>("AssignRolesTicks", assignRolesTicksNode.Value);

			if (nodes.TryGetValue("AttackForceTicks", out var attackForceTicksNode))
				attackForceTicks = FieldLoader.GetValue<int>("AttackForceTicks", attackForceTicksNode.Value);

			if (nodes.TryGetValue("MinAttackForceDelayTicks", out var minAttackForceDelayTicksNode))
				minAttackForceDelayTicks = FieldLoader.GetValue<int>("MinAttackForceDelayTicks", minAttackForceDelayTicksNode.Value);

			if (nodes.TryGetValue("Squads", out var squadsNode))
			{
				Squads.Clear();
				foreach (var n in squadsNode.Nodes)
					Squads.Add(Squad.Deserialize(bot, this, n.Value));
			}
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			constructionYardBuildings.Dispose();
		}
	}
}
