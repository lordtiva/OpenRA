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

namespace OpenRA.Mods.Common.Traits
{
	public enum BotDecisionKind { Build, Train }

	/// <summary>
	/// Implemented by an <see cref="IBot"/> whose strategic choices (what to produce, where the
	/// army goes) come from an external agent. Bot modules ask it instead of their own heuristics
	/// and keep doing the execution (placement, harvesting, micro).
	/// </summary>
	public interface IBotDecisionBroker
	{
		/// <summary>
		/// True when the agent decides. Modules then skip heuristics that would make strategic
		/// choices on its behalf (attack waves, idle-unit caps, cash gates, production requests).
		/// </summary>
		bool ExternalControl { get; }

		/// <summary>
		/// Choose what a free production queue starts. Returns null to produce nothing for now.
		/// <paramref name="nativeChoice"/> is the module's own heuristic, used when the agent
		/// is the built-in logic (and reported as a teacher decision).
		/// </summary>
		ActorInfo DecideProduction(BotDecisionKind kind, ProductionQueue queue, Func<ActorInfo> nativeChoice);
	}
}
