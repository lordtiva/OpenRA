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
using OpenRA.Traits;
using RLProto = OpenRA.Mods.Common.RL;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>
	/// Converts protobuf AgentAction commands into OpenRA Orders and queues them via IBot.
	/// </summary>
	public sealed class ActionHandler
	{
		readonly World world;
		readonly Player player;

		public ActionHandler(World world, Player player)
		{
			this.world = world;
			this.player = player;
		}

		public void ProcessAction(RLProto.AgentAction agentAction, IBot bot)
		{
			foreach (var command in agentAction.Commands)
			{
				if (command.Action == RLProto.ActionType.Patrol)
				{
					QueuePatrol(command, bot);
					continue;
				}

				var order = ConvertCommand(command);
				if (order != null)
					bot.QueueOrder(order);
			}
		}

		Order ConvertCommand(RLProto.Command cmd)
		{
			switch (cmd.Action)
			{
				case RLProto.ActionType.NoOp:
					return null;

				case RLProto.ActionType.Move:
					return CreateMoveOrder(cmd);

				case RLProto.ActionType.AttackMove:
					return CreateAttackMoveOrder(cmd);

				case RLProto.ActionType.Attack:
					return CreateAttackOrder(cmd);

				case RLProto.ActionType.Stop:
					return CreateStopOrder(cmd);

				case RLProto.ActionType.Harvest:
					return CreateHarvestOrder(cmd);

				case RLProto.ActionType.Build:
				case RLProto.ActionType.Train:
					return CreateProductionOrder(cmd);

				case RLProto.ActionType.Deploy:
					return CreateDeployOrder(cmd);

				case RLProto.ActionType.Sell:
					return CreateSellOrder(cmd);

				case RLProto.ActionType.Repair:
					return CreateRepairOrder(cmd);

				case RLProto.ActionType.PlaceBuilding:
					return CreatePlaceBuildingOrder(cmd);

				case RLProto.ActionType.CancelProduction:
					return CreateCancelProductionOrder(cmd);

				case RLProto.ActionType.SetRallyPoint:
					return CreateSetRallyPointOrder(cmd);

				case RLProto.ActionType.Guard:
					return CreateGuardOrder(cmd);

				case RLProto.ActionType.SetStance:
					return CreateSetStanceOrder(cmd);

				case RLProto.ActionType.EnterTransport:
					return CreateEnterTransportOrder(cmd);

				case RLProto.ActionType.Unload:
					return CreateUnloadOrder(cmd);

				case RLProto.ActionType.PowerDown:
					return CreatePowerDownOrder(cmd);

				case RLProto.ActionType.SetPrimary:
					return CreateSetPrimaryOrder(cmd);

				case RLProto.ActionType.Surrender:
					return new Order("Surrender", player.PlayerActor, false);

				case RLProto.ActionType.ArmyAttackMove:
					return CreateArmyAttackMoveOrder(cmd);

				case RLProto.ActionType.SupportPower:
					return CreateSupportPowerOrder(cmd);

				case RLProto.ActionType.FastAdvance:
					// Handled by ExternalBotBridge directly (not an Order)
					return null;

				default:
					Log.Write("rl-bridge", $"Unknown action type: {cmd.Action}");
					return null;
			}
		}

		Order CreateMoveOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			var cell = new CPos(cmd.TargetX, cmd.TargetY);
			var target = Target.FromCell(world, cell);

			return new Order("Move", subject, target, cmd.Queued);
		}

		Order CreateAttackMoveOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			var cell = new CPos(cmd.TargetX, cmd.TargetY);
			var target = Target.FromCell(world, cell);

			return new Order("AttackMove", subject, target, cmd.Queued);
		}

		// Fase 2 (auditoria 2026-08-24): attack-move de TODAS las unidades de
		// combate propias hacia la celda objetivo. Una sola decision mueve el
		// ejercito entero: sin esto el agente no puede ejecutar una batalla
		// con 1 comando cada 160 ticks. Los cosechadores y el MCV quedan fuera.
		Order CreateArmyAttackMoveOrder(RLProto.Command cmd)
		{
			if (cmd.TargetX < 0 || cmd.TargetY < 0 ||
				cmd.TargetX >= world.Map.MapSize.Width ||
				cmd.TargetY >= world.Map.MapSize.Height)
				return null;

			var cell = new CPos(cmd.TargetX, cmd.TargetY);
			var target = Target.FromCell(world, cell);
			Order last = null;
			foreach (var unit in world.ActorsHavingTrait<IPositionable>())
			{
				if (unit.Owner != player || unit.IsDead || !unit.IsInWorld)
					continue;
				if (unit.TraitOrDefault<Harvester>() != null)
					continue;
				last = new Order("AttackMove", unit, target, cmd.Queued);
				world.IssueOrder(last);
			}
			return last;
		}

		Order CreateAttackOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			if (cmd.TargetActorId != 0)
			{
				var targetActor = world.GetActorById(cmd.TargetActorId);
				if (targetActor == null || targetActor.IsDead || !targetActor.IsInWorld)
					return null;

				var target = Target.FromActor(targetActor);
				// Aliados: engineer Capture / spy Infiltrate without a new ActionType.
				if (subject.TraitOrDefault<Captures>() != null)
					return new Order("CaptureActor", subject, target, cmd.Queued);
				if (string.Equals(subject.Info.Name, "spy", StringComparison.OrdinalIgnoreCase))
					return new Order("Infiltrate", subject, target, cmd.Queued);
				return new Order("Attack", subject, target, cmd.Queued);
			}

			// Attack-ground at position
			var cell = new CPos(cmd.TargetX, cmd.TargetY);
			return new Order("ForceAttack", subject, Target.FromCell(world, cell), cmd.Queued);
		}

		Order CreateStopOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			return new Order("Stop", subject, false);
		}

		Order CreateHarvestOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			if (cmd.TargetX != 0 || cmd.TargetY != 0)
			{
				var cell = new CPos(cmd.TargetX, cmd.TargetY);
				return new Order("Harvest", subject, Target.FromCell(world, cell), cmd.Queued);
			}

			return new Order("Harvest", subject, cmd.Queued);
		}

		Order CreateProductionOrder(RLProto.Command cmd)
		{
			if (string.IsNullOrEmpty(cmd.ItemType))
			{
				Log.Write("rl-bridge", "Production command missing item_type");
				return null;
			}

			// Find a production structure that can build this item
			foreach (var actor in world.ActorsHavingTrait<ProductionQueue>())
			{
				if (actor.Owner != player || actor.IsDead || !actor.IsInWorld)
					continue;

				foreach (var queue in actor.TraitsImplementing<ProductionQueue>())
				{
					foreach (var buildable in queue.BuildableItems())
					{
						if (string.Equals(buildable.Name, cmd.ItemType, StringComparison.OrdinalIgnoreCase))
							return Order.StartProduction(actor, cmd.ItemType, 1, cmd.Queued);
					}
				}
			}

			Log.Write("rl-bridge", $"Cannot produce '{cmd.ItemType}': no capable production structure found");
			return null;
		}

		Order CreateDeployOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			// Chrono Tank (Germany): PortableChronoTeleport to the sampled cell.
			if (string.Equals(subject.Info.Name, "ctnk", StringComparison.OrdinalIgnoreCase)
				&& (cmd.TargetX != 0 || cmd.TargetY != 0))
			{
				var cell = new CPos(cmd.TargetX, cmd.TargetY);
				if (world.Map.Contains(cell))
					return new Order("PortableChronoTeleport", subject, Target.FromCell(world, cell), cmd.Queued);
			}

			return new Order("DeployTransform", subject, false);
		}

		void QueuePatrol(RLProto.Command cmd, IBot bot)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return;

			var destCell = new CPos(cmd.TargetX, cmd.TargetY);
			if (!world.Map.Contains(destCell))
				return;

			var dest = Target.FromCell(world, destCell);
			var home = Target.FromCell(world, subject.Location);
			bot.QueueOrder(new Order("AttackMove", subject, dest, false));
			bot.QueueOrder(new Order("AttackMove", subject, home, true));
		}

		Order CreateSupportPowerOrder(RLProto.Command cmd)
		{
			var spm = player.PlayerActor.TraitOrDefault<SupportPowerManager>();
			if (spm == null)
				return null;

			var key = cmd.ItemType;
			if (string.IsNullOrEmpty(key))
			{
				foreach (var kv in spm.Powers)
				{
					if (kv.Value != null && kv.Value.Ready
						&& kv.Key.IndexOf("Gps", StringComparison.OrdinalIgnoreCase) < 0)
					{
						key = kv.Key;
						break;
					}
				}
			}

			if (string.IsNullOrEmpty(key) || !spm.Powers.ContainsKey(key) || !spm.Powers[key].Ready)
				return null;

			var dest = new CPos(cmd.TargetX, cmd.TargetY);
			if (!world.Map.Contains(dest))
			{
				dest = CPos.Zero;
				foreach (var a in world.Actors)
				{
					if (a.Owner != player || a.IsDead || !a.IsInWorld)
						continue;
					if (string.Equals(a.Info.Name, "fact", StringComparison.OrdinalIgnoreCase))
					{
						dest = a.Location;
						break;
					}
				}

				if (!world.Map.Contains(dest))
					return null;
			}

			var order = new Order(key, player.PlayerActor, Target.FromCell(world, dest), false)
			{
				SuppressVisualFeedback = true
			};

			// Chronoshift: ExtraLocation is the SOURCE footprint; actor_id is a
			// unit standing in that pack. Without it, (0,0) would be the source.
			var sourceActor = world.GetActorById(cmd.ActorId);
			if (sourceActor == null || sourceActor.IsDead || !sourceActor.IsInWorld
				|| sourceActor.Owner != player)
			{
				sourceActor = null;
				foreach (var a in world.ActorsHavingTrait<IPositionable>())
				{
					if (a.Owner != player || a.IsDead || !a.IsInWorld)
						continue;
					if (a.TraitOrDefault<Harvester>() != null)
						continue;
					if (string.Equals(a.Info.Name, "mcv", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(a.Info.Name, "fact", StringComparison.OrdinalIgnoreCase))
						continue;
					sourceActor = a;
					break;
				}
			}

			order.ExtraLocation = sourceActor != null ? sourceActor.Location : dest;

			return order;
		}

		Order CreateSellOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			return new Order("Sell", subject, false) { IsImmediate = true };
		}

		Order CreateRepairOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			return new Order("RepairBuilding", subject, false) { IsImmediate = true };
		}

		Order CreatePlaceBuildingOrder(RLProto.Command cmd)
		{
			if (string.IsNullOrEmpty(cmd.ItemType))
			{
				Log.Write("rl-bridge", "PlaceBuilding command missing item_type");
				return null;
			}

			// Find the production queue that has this building ready
			Actor producer = null;
			foreach (var actor in world.ActorsHavingTrait<ProductionQueue>())
			{
				if (actor.Owner != player || actor.IsDead || !actor.IsInWorld)
					continue;

				foreach (var queue in actor.TraitsImplementing<ProductionQueue>())
				{
					foreach (var item in queue.AllQueued())
					{
						if (item.Done && string.Equals(item.Item, cmd.ItemType, StringComparison.OrdinalIgnoreCase))
						{
							producer = actor;
							break;
						}
					}

					if (producer != null) break;
				}

				if (producer != null) break;
			}

			if (producer == null)
			{
				Log.Write("rl-bridge", $"Cannot place '{cmd.ItemType}': no completed building in any production queue");
				return null;
			}

			// Resolve building info for placement validation
			var actorInfo = world.Map.Rules.Actors[cmd.ItemType.ToLowerInvariant()];
			var bi = actorInfo.TraitInfoOrDefault<BuildingInfo>();

			// Try agent's requested position first
			var requestedCell = new CPos(cmd.TargetX, cmd.TargetY);
			if (cmd.TargetX != 0 || cmd.TargetY != 0)
			{
				if (world.CanPlaceBuilding(requestedCell, actorInfo, bi, null)
					&& bi.IsCloseEnoughToBase(world, player, actorInfo, requestedCell))
				{
					Log.Write("rl-bridge", $"Placing '{cmd.ItemType}' at requested ({cmd.TargetX},{cmd.TargetY})");
					return MakePlaceOrder(cmd.ItemType, requestedCell, producer);
				}
			}

			// Auto-find: search outward from base center
			var baseCenter = GetBaseCenter();
			var foundCell = FindPlacementCell(actorInfo, bi, baseCenter);
			if (foundCell.HasValue)
			{
				Log.Write("rl-bridge", $"Auto-placed '{cmd.ItemType}' at ({foundCell.Value.X},{foundCell.Value.Y}) near base center ({baseCenter.X},{baseCenter.Y})");
				return MakePlaceOrder(cmd.ItemType, foundCell.Value, producer);
			}

			Log.Write("rl-bridge", $"Cannot place '{cmd.ItemType}': no valid cell found near base");
			return null;
		}

		Order MakePlaceOrder(string itemType, CPos cell, Actor producer)
		{
			return new Order("PlaceBuilding", player.PlayerActor,
				Target.FromCell(world, cell), false)
			{
				TargetString = itemType,
				ExtraData = producer.ActorID,
			};
		}

		CPos GetBaseCenter()
		{
			// Use Construction Yard location as base center
			foreach (var actor in world.ActorsHavingTrait<BaseProvider>())
			{
				if (actor.Owner == player && !actor.IsDead && actor.IsInWorld)
					return actor.Location;
			}

			// Fallback: first owned building
			foreach (var actor in world.ActorsHavingTrait<Building>())
			{
				if (actor.Owner == player && !actor.IsDead && actor.IsInWorld)
					return actor.Location;
			}

			return new CPos(world.Map.MapSize.Width / 2, world.Map.MapSize.Height / 2);
		}

		CPos? FindPlacementCell(ActorInfo actorInfo, BuildingInfo bi, CPos center)
		{
			// Search in expanding rings from center, up to 20 cells out
			foreach (var cell in world.Map.FindTilesInAnnulus(center, 0, 20))
			{
				if (!world.CanPlaceBuilding(cell, actorInfo, bi, null))
					continue;

				if (!bi.IsCloseEnoughToBase(world, player, actorInfo, cell))
					continue;

				return cell;
			}

			return null;
		}

		Order CreateCancelProductionOrder(RLProto.Command cmd)
		{
			if (string.IsNullOrEmpty(cmd.ItemType))
			{
				Log.Write("rl-bridge", "CancelProduction command missing item_type");
				return null;
			}

			// Find the production queue producing this item
			foreach (var actor in world.ActorsHavingTrait<ProductionQueue>())
			{
				if (actor.Owner != player || actor.IsDead || !actor.IsInWorld)
					continue;

				foreach (var queue in actor.TraitsImplementing<ProductionQueue>())
				{
					foreach (var item in queue.AllQueued())
					{
						if (string.Equals(item.Item, cmd.ItemType, StringComparison.OrdinalIgnoreCase))
							return Order.CancelProduction(actor, cmd.ItemType, 1);
					}
				}
			}

			Log.Write("rl-bridge", $"Cannot cancel '{cmd.ItemType}': not in any production queue");
			return null;
		}

		Order CreateSetRallyPointOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			var cell = new CPos(cmd.TargetX, cmd.TargetY);
			return new Order("SetRallyPoint", subject, Target.FromCell(world, cell), false);
		}

		Order CreateGuardOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			var target = world.GetActorById(cmd.TargetActorId);
			if (target == null || target.IsDead || !target.IsInWorld)
				return null;

			return new Order("Guard", subject, Target.FromActor(target), cmd.Queued);
		}

		Order CreateSetStanceOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			// Stance encoded in target_x: 0=HoldFire, 1=ReturnFire, 2=Defend, 3=AttackAnything
			return new Order("SetUnitStance", subject, false)
			{
				ExtraData = (uint)Math.Clamp(cmd.TargetX, 0, 3)
			};
		}

		Order CreateEnterTransportOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			var target = world.GetActorById(cmd.TargetActorId);
			if (target == null || target.IsDead || !target.IsInWorld)
				return null;

			return new Order("EnterTransport", subject, Target.FromActor(target), cmd.Queued);
		}

		Order CreateUnloadOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			return new Order("Unload", subject, false);
		}

		Order CreatePowerDownOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			return new Order("PowerDown", subject, false);
		}

		Order CreateSetPrimaryOrder(RLProto.Command cmd)
		{
			var subject = world.GetActorById(cmd.ActorId);
			if (subject == null || subject.IsDead || !subject.IsInWorld)
				return null;

			return new Order("PrimaryProducer", subject, false);
		}
	}
}
