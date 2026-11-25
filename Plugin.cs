using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace PetPatch
{
	[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
	public class Plugin : BaseUnityPlugin
	{
		public const string PluginGUID    = "Mordel2Berde.PetPatch";
		public const string PluginName    = "PetPatch";
		public const string PluginVersion = "1.4.0";

		internal static new BepInEx.Logging.ManualLogSource Logger = null!;

		internal static ConfigEntry<bool> Enabled = null!;
		internal static ConfigEntry<float> BodyMargin = null!;
		internal static ConfigEntry<float> ClearanceFactor = null!;
		internal static ConfigEntry<float> PushSpeed = null!;
		internal static ConfigEntry<bool> KeepTurningRoom = null!;
		internal static ConfigEntry<float> IdleMinSeconds = null!;
		internal static ConfigEntry<float> IdleMaxSeconds = null!;
		internal static ConfigEntry<bool> StopForPlayers = null!;
		internal static ConfigEntry<bool> StopForPets = null!;
		internal static ConfigEntry<bool> ShowFollowedPlayer = null!;
		internal static ConfigEntry<bool> LogBodySizes = null!;

		private readonly Harmony _harmony = new Harmony(PluginGUID);

		private void Awake()
		{
			Logger = base.Logger;

			Enabled = Config.Bind("General", "Enabled", true,
				"Keep the body of tamed creatures out of buildings and obstacles.");
			BodyMargin = Config.Bind("General", "BodyMargin", 0.15f,
				new ConfigDescription("Clearance added around the skeleton, in meters. Increase it if a body part still clips.",
					new AcceptableValueRange<float>(0f, 1f)));
			ClearanceFactor = Config.Bind("General", "ClearanceFactor", 1.1f,
				new ConfigDescription("Multiplies the distance kept between the center of the creature and obstacles, after BodyMargin. " +
					"The width never exceeds the collision capsule, so that doorways stay passable.",
					new AcceptableValueRange<float>(1f, 2f)));
			PushSpeed = Config.Bind("General", "PushSpeed", 1.5f,
				new ConfigDescription("Speed at which a clipping creature is moved out of an obstacle, in m/s.",
					new AcceptableValueRange<float>(0.1f, 5f)));
			KeepTurningRoom = Config.Bind("General", "KeepTurningRoom", true,
				"Tamed creatures only roam to places they can actually reach, with enough room to turn around without their body " +
				"entering an obstacle. Walking along an obstacle is still allowed.");
			IdleMinSeconds = Config.Bind("General", "IdleMinSeconds", 5f,
				new ConfigDescription("Minimum time a roaming tamed creature stays in place after arriving or being interrupted, in seconds.",
					new AcceptableValueRange<float>(0f, 600f)));
			IdleMaxSeconds = Config.Bind("General", "IdleMaxSeconds", 45f,
				new ConfigDescription("Maximum time a roaming tamed creature stays in place after arriving or being interrupted, in seconds.",
					new AcceptableValueRange<float>(0f, 600f)));
			StopForPlayers = Config.Bind("Traffic", "StopForPlayers", true,
				"Tamed creatures stop instead of pushing a player standing in their way.");
			StopForPets = Config.Bind("Traffic", "StopForPets", true,
				"Tamed creatures stop instead of pushing another tamed creature standing in their way.");
			ShowFollowedPlayer = Config.Bind("Display", "ShowFollowedPlayer", true,
				"Show the name of the followed player after the name of a tamed creature: Wolf -> Ragnar.");
			LogBodySizes = Config.Bind("Debug", "LogBodySizes", false,
				"Log the body size measured for each tamed creature.");

			BodyClearance.Initialize();
			_harmony.PatchAll();
		}

		private void OnDestroy()
		{
			_harmony.UnpatchSelf();
			BodyClearance.Shutdown();
		}
	}

	/// <summary>Makes a roaming creature stay in place for a random time.</summary>
	internal static class IdleWait
	{
		private static readonly AccessTools.FieldRef<BaseAI, bool> Reached = AccessTools.FieldRefAccess<BaseAI, bool>("m_reachedRandomMoveTarget");
		private static readonly AccessTools.FieldRef<BaseAI, float> Timer = AccessTools.FieldRefAccess<BaseAI, float>("m_randomMoveUpdateTimer");

		internal static void Start(BaseAI ai)
		{
			Reached(ai) = true;
			Timer(ai) = Random.Range(Plugin.IdleMinSeconds.Value, Plugin.IdleMaxSeconds.Value);
		}
	}

	/// <summary>
	/// The game draws a random point around the creature without checking that it can be reached.
	/// As a partial path is accepted, a point behind a wall or a closed door brings the creature
	/// against that wall or door, where it then stays.
	///
	/// A point is now only kept if a full path leads to it, and the room to turn around is tested
	/// where this path really ends. Otherwise closer points are tried, then the creature stays put.
	/// </summary>
	[HarmonyPatch(typeof(BaseAI), "IsValidRandomMovePoint")]
	internal static class RandomDestinationPatch
	{
		private static readonly float[] DistanceScales = { 1f, 1f, 0.75f, 0.75f, 0.5f, 0.5f, 0.25f, 0.25f };
		private static readonly Func<BaseAI, Vector3, bool> IsValidPoint =
			AccessTools.MethodDelegate<Func<BaseAI, Vector3, bool>>(AccessTools.Method(typeof(BaseAI), "IsValidRandomMovePoint"));
		private static readonly List<Vector3> Path = new List<Vector3>();
		// IsValidPoint comes back here for each candidate.
		private static bool _searching;

		private static void Postfix(BaseAI __instance, Vector3 point, ref bool __result, Character ___m_character,
			Pathfinding.AgentType ___m_pathAgentType, ref Vector3 ___m_randomMoveTarget)
		{
			if (_searching || !__result || !___m_character.IsTamed() || !BodyClearance.IsDestinationCheckActive(___m_character))
				return;

			_searching = true;
			try
			{
				if (FindDestination(__instance, ___m_character, ___m_pathAgentType, point, out Vector3 destination))
				{
					___m_randomMoveTarget = destination;
					return;
				}
			}
			finally
			{
				_searching = false;
			}

			__result = false;
			// The AI keeps its last movement while it looks for a point.
			__instance.StopMoving();
			IdleWait.Start(__instance);
		}

		private static bool FindDestination(BaseAI ai, Character character, Pathfinding.AgentType agentType, Vector3 point, out Vector3 destination)
		{
			Vector3 origin = character.transform.position;
			Vector3 offset = point - origin;
			offset.y = 0f;

			for (int i = 0; i < DistanceScales.Length; i++)
			{
				Quaternion rotation = i == 0 ? Quaternion.identity : Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
				Vector3 candidate = origin + rotation * offset * DistanceScales[i];
				if (i > 0 && !IsValidPoint(ai, candidate))
					continue;

				// The height of the drawn point is unreliable, hence the height of the creature.
				if (!Pathfinding.instance.GetPath(origin, candidate, Path, agentType, requireFullPath: true, cleanup: false) || Path.Count == 0)
					continue;

				// The navmesh ignores creatures. Without this, a creature heading back to its patrol
				// point would draw the same direction again and again, into the pet that blocks it.
				if (BodyClearance.IsWayBlocked(character, FirstLeg(origin)))
					continue;

				Vector3 end = Path[Path.Count - 1];
				if (BodyClearance.HasRoomAt(character, end))
				{
					destination = end;
					return true;
				}
			}

			destination = point;
			return false;
		}

		/// <summary>Direction of the start of the path, its first point being the creature itself.</summary>
		private static Vector3 FirstLeg(Vector3 origin)
		{
			foreach (Vector3 p in Path)
			{
				Vector3 leg = p - origin;
				leg.y = 0f;
				if (leg.sqrMagnitude > 0.09f)
					return leg;
			}
			return Vector3.zero;
		}
	}

	/// <summary>
	/// By default a creature picks its next destination on a fixed schedule, including the walk.
	/// It now waits a random time from the moment it arrives.
	/// </summary>
	[HarmonyPatch(typeof(BaseAI), "RandomMovement")]
	internal static class ArrivalWaitPatch
	{
		private static void Prefix(bool ___m_reachedRandomMoveTarget, out bool __state)
		{
			__state = ___m_reachedRandomMoveTarget;
		}

		private static void Postfix(BaseAI __instance, Vector3 centerPoint, bool __state, bool ___m_reachedRandomMoveTarget, Character ___m_character)
		{
			if (__state || !___m_reachedRandomMoveTarget || !Plugin.Enabled.Value || !___m_character.IsTamed() || __instance.IsAlerted())
				return;
			// Same condition as the game: a creature far from its patrol point heads straight back.
			if (Utils.DistanceXZ(centerPoint, ___m_character.transform.position) > __instance.m_randomMoveRange * 2f)
				return;

			IdleWait.Start(__instance);
		}
	}

	/// <summary>
	/// A follower stops at a given distance from its target, wherever that is. When there is no
	/// room to turn there, it keeps coming closer: the target is usually standing in the open.
	/// </summary>
	[HarmonyPatch(typeof(BaseAI), "Follow")]
	internal static class FollowStopPatch
	{
		private static void Postfix(BaseAI __instance, GameObject go, Character ___m_character)
		{
			if (go == null || !___m_character.IsTamed() || ___m_character.HaveRider() || ___m_character.GetMoveDir().sqrMagnitude > 0.0001f)
				return;
			if (!BodyClearance.LacksTurningRoom(___m_character, out float minDistance))
				return;

			Vector3 toTarget = go.transform.position - ___m_character.transform.position;
			toTarget.y = 0f;
			if (toTarget.magnitude > minDistance)
				__instance.MoveTowards(toTarget, run: false);
		}
	}

	/// <summary>
	/// Shows the name of the followed player after the name of a tamed creature, above its
	/// health bar and when aiming at it: "Wolf -> Ragnar".
	///
	/// The followed GameObject only exists on the owner of the creature, but the owner also writes
	/// the player name to the ZDO, which every client receives. Reading it there makes the display
	/// correct for all players, whoever simulates the creature.
	/// </summary>
	internal static class FollowedPlayer
	{
		private static readonly AccessTools.FieldRef<Character, ZNetView> NViewOf = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");

		/// <summary>" -> PlayerName" if this tamed creature follows a player, "" otherwise.</summary>
		internal static string Suffix(Character character)
		{
			if (!Plugin.ShowFollowedPlayer.Value || character == null || character.IsPlayer() || !character.IsTamed())
				return "";
			ZNetView nview = NViewOf(character);
			if (nview == null || !nview.IsValid())
				return "";
			string follow = nview.GetZDO().GetString(ZDOVars.s_follow);
			return string.IsNullOrEmpty(follow) ? "" : " -> " + follow;
		}
	}

	/// <summary>
	/// Name above the health bar. It is written by EnemyHud.UpdateHuds from GetHoverName, but
	/// CLLC then overwrites it on each frame with a name it caches when the HUD appears. So the
	/// suffix is appended to the final text, after every other patch.
	/// </summary>
	[HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
	[HarmonyPriority(Priority.Last)]
	[HarmonyAfter("org.bepinex.plugins.creaturelevelcontrol")]
	internal static class FollowedPlayerHudPatch
	{
		private static readonly System.Reflection.FieldInfo HudsField = AccessTools.Field(typeof(EnemyHud), "m_huds");
		private static readonly Type HudDataType = AccessTools.Inner(typeof(EnemyHud), "HudData");
		private static readonly AccessTools.FieldRef<object, Character> CharacterOf = AccessTools.FieldRefAccess<Character>(HudDataType, "m_character");
		private static readonly AccessTools.FieldRef<object, GameObject> GuiOf = AccessTools.FieldRefAccess<GameObject>(HudDataType, "m_gui");
		private static readonly AccessTools.FieldRef<object, TMPro.TextMeshProUGUI> NameOf = AccessTools.FieldRefAccess<TMPro.TextMeshProUGUI>(HudDataType, "m_name");

		private static void Postfix(EnemyHud __instance)
		{
			if (!Plugin.ShowFollowedPlayer.Value || !(HudsField.GetValue(__instance) is System.Collections.IDictionary huds))
				return;
			foreach (object hud in huds.Values)
			{
				GameObject gui = GuiOf(hud);
				TMPro.TextMeshProUGUI name = NameOf(hud);
				if (gui == null || !gui.activeSelf || name == null)
					continue;
				string suffix = FollowedPlayer.Suffix(CharacterOf(hud));
				if (suffix.Length > 0 && !name.text.EndsWith(suffix))
					name.text += suffix;
			}
		}
	}

	/// <summary>Text shown when aiming at the creature: "Wolf -> Ragnar ( Tame, Happy )".</summary>
	[HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverText))]
	internal static class FollowedPlayerHoverPatch
	{
		private static void Postfix(Tameable __instance, ref string __result)
		{
			if (string.IsNullOrEmpty(__result))
				return;
			string suffix = FollowedPlayer.Suffix(__instance.GetComponent<Character>());
			if (suffix.Length == 0)
				return;
			string name = __instance.GetName();
			__result = __result.StartsWith(name) ? name + suffix + __result.Substring(name.Length) : __result;
		}
	}

	/// <summary>
	/// A creature only collides with the environment through its capsule, whose radius is far
	/// smaller than the length of its body (Lox: radius 1.5 m, muzzle 4.3 m from the center), and
	/// pathfinding uses an even smaller radius, so paths run along walls. Head and rump therefore
	/// go through whatever the creature walks up to or turns next to.
	///
	/// Instead of changing colliders or the navmesh (a larger one would block doorways), this
	/// tests a box matching the real body and steers the creature away from what it overlaps.
	///
	/// Creatures also push whoever stands in their way, whatever their mass, because walking
	/// imposes a velocity on each tick. So a moving creature looks ahead and interrupts its movement
	/// when a player or another tamed creature is in the way. The next movement picked by the AI
	/// settles the conflict.
	/// </summary>
	[HarmonyPatch(typeof(Character), "UpdateMotion")]
	internal static class BodyClearance
	{
		private const float ScanInterval = 0.1f;
		private const float DeadZone = 0.03f;
		private const float MaxCorrection = 1f;
		// Ignores floors, ceilings and the tilted colliders of stairs and ramps.
		private const float MaxVerticalNormal = 0.4f;
		// Beyond this distance a transform is a rig helper rather than a body part.
		private const float MaxBoneDistance = 10f;
		// A situation that can't be solved (narrow passage, unusual stairs) must not trap the creature.
		private const float GiveUpAfter = 1.5f;
		private const float PauseDuration = 6f;
		private const float ProgressStep = 0.02f;

		// The scan interval has to be covered by the look-ahead so that a fast creature stops in time.
		private const float LookAheadBase = 0.3f;
		private const float LookAheadPerSpeed = 0.15f;

		// 30 degrees apart, the ends of the body sweep the whole turning circle.
		private const int TurnSamples = 12;
		private const float RoomCheckInterval = 0.25f;

		private sealed class State
		{
			public bool Protected;
			public float Radius, Front, Rear, HalfWidth, Bottom, Top;
			public float NextScan, PauseUntil;
			public Vector3 Remaining, ContactDir;
			public float BestDepth = float.MaxValue, StuckTime;

			public float NextTrafficScan;
			public bool Blocked;

			public float NextRoomCheck;
			public bool HasRoomHere = true;
		}

		private static readonly ConditionalWeakTable<Character, State> States = new ConditionalWeakTable<Character, State>();
		private static readonly AccessTools.FieldRef<Character, CapsuleCollider> CapsuleOf = AccessTools.FieldRefAccess<Character, CapsuleCollider>("m_collider");
		private static readonly Collider[] Overlaps = new Collider[32];
		private static int _obstacleMask;
		private static int _characterMask;
		private static GameObject? _probeObject;
		private static BoxCollider? _probe;

		internal static void Initialize()
		{
			// Terrain is left out: creatures have to be able to walk up slopes.
			// So are ships: their hull would push away a creature standing on the deck.
			_obstacleMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece");
			// character_net: characters owned by another client. character_noenv: the body of the Lox.
			_characterMask = LayerMask.GetMask("character", "character_net", "character_noenv");

			// ComputePenetration needs a real collider. This one only serves as a shape,
			// its pose is given with each call, so it sits out of the way of everything.
			_probeObject = new GameObject("PetPatch_Probe");
			_probeObject.hideFlags = HideFlags.HideAndDontSave;
			_probeObject.layer = LayerMask.NameToLayer("Ignore Raycast");
			_probeObject.transform.position = new Vector3(0f, -10000f, 0f);
			Object.DontDestroyOnLoad(_probeObject);
			_probe = _probeObject.AddComponent<BoxCollider>();
		}

		internal static void Shutdown()
		{
			if (_probeObject != null)
				Object.Destroy(_probeObject);
		}

		private static State GetState(Character character) => States.GetValue(character, c => Measure(c, CapsuleOf(c)));

		// A creature in the water has to be able to get out by any means.
		internal static bool IsDestinationCheckActive(Character character) =>
			Plugin.Enabled.Value && Plugin.KeepTurningRoom.Value && !character.IsFlying() && !character.IsSwimming();

		internal static bool HasRoomAt(Character character, Vector3 position)
		{
			State state = GetState(character);
			return !state.Protected || HasTurningRoom(state, position);
		}

		internal static bool LacksTurningRoom(Character character, out float minDistance)
		{
			State state = GetState(character);
			minDistance = Mathf.Max(state.Front, state.Radius) + 1f;
			if (!Plugin.Enabled.Value || !Plugin.KeepTurningRoom.Value || !state.Protected || character.IsFlying())
				return false;

			float time = Time.time;
			if (time >= state.NextRoomCheck)
			{
				state.NextRoomCheck = time + RoomCheckInterval;
				state.HasRoomHere = HasTurningRoom(state, character.transform.position);
			}
			return !state.HasRoomHere;
		}

		private static bool HasTurningRoom(State state, Vector3 position)
		{
			float offset = (state.Front + state.Rear) * 0.5f;
			var halfExtents = new Vector3(state.HalfWidth, (state.Top - state.Bottom) * 0.5f, (state.Front - state.Rear) * 0.5f);
			Vector3 pivot = position + Vector3.up * ((state.Bottom + state.Top) * 0.5f);

			for (int i = 0; i < TurnSamples; i++)
			{
				Quaternion rotation = Quaternion.Euler(0f, i * 360f / TurnSamples, 0f);
				if (Physics.CheckBox(pivot + rotation * Vector3.forward * offset, halfExtents, rotation, _obstacleMask, QueryTriggerInteraction.Ignore))
					return false;
			}
			return true;
		}

		private static void Prefix(Character __instance, float dt, ref Vector3 ___m_moveDir, Rigidbody ___m_body)
		{
			if (!Plugin.Enabled.Value || !__instance.IsTamed())
				return;

			State state = GetState(__instance);
			if (!CanAdjust(__instance, ___m_body))
				return;

			float time = Time.time;
			if (Plugin.StopForPlayers.Value || Plugin.StopForPets.Value)
				AvoidCharacters(__instance, state, time, ref ___m_moveDir, ___m_body);

			// Comes last: an obstacle has priority over what the avoidance above asks for.
			if (state.Protected && time >= state.PauseUntil)
				KeepBodyClear(__instance, state, time, dt, ref ___m_moveDir, ___m_body);
		}

		private static void AvoidCharacters(Character character, State state, float time, ref Vector3 moveDir, Rigidbody body)
		{
			if (character.InAttack())
			{
				state.Blocked = false;
				return;
			}

			Vector3 flatMove = new Vector3(moveDir.x, 0f, moveDir.z);
			// Stationary creatures don't scan. A movement cancelled on the previous tick also reads
			// as zero here: the result is kept until the AI asks to move again.
			if (time >= state.NextTrafficScan && flatMove.sqrMagnitude > 0.0001f)
			{
				state.NextTrafficScan = time + ScanInterval;
				state.Blocked = IsPathBlocked(character, state, flatMove.normalized, body);

				// Interrupted while roaming: the creature waits instead of drawing a new destination.
				// This has no effect on a creature following someone or fighting.
				BaseAI ai = character.GetBaseAI();
				if (state.Blocked && ai != null && !ai.IsAlerted())
					IdleWait.Start(ai);
			}

			if (state.Blocked)
				moveDir = Vector3.zero;
		}

		private static readonly AccessTools.FieldRef<Character, Rigidbody> BodyOf = AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");

		/// <summary>True when a player or a pet stands right in front of the creature in this direction.</summary>
		internal static bool IsWayBlocked(Character character, Vector3 direction)
		{
			if (!Plugin.Enabled.Value || !(Plugin.StopForPlayers.Value || Plugin.StopForPets.Value))
				return false;
			direction.y = 0f;
			Rigidbody body = BodyOf(character);
			if (direction.sqrMagnitude < 0.0001f || body == null)
				return false;
			return IsPathBlocked(character, GetState(character), direction.normalized, body);
		}

		private static bool IsPathBlocked(Character character, State state, Vector3 direction, Rigidbody body)
		{
			Vector3 position = character.transform.position;

			Vector3 velocity = body.linearVelocity;
			velocity.y = 0f;
			float length = Mathf.Max(state.Front, state.Radius) + LookAheadBase + velocity.magnitude * LookAheadPerSpeed;
			const float bottom = 0.1f;
			float top = Mathf.Max(state.Top, 0.5f);

			Vector3 center = position + direction * (length * 0.5f) + Vector3.up * ((bottom + top) * 0.5f);
			var halfExtents = new Vector3(state.Radius, (top - bottom) * 0.5f, length * 0.5f);
			int count = Physics.OverlapBoxNonAlloc(center, halfExtents, Overlaps, Quaternion.LookRotation(direction), _characterMask, QueryTriggerInteraction.Ignore);

			for (int i = 0; i < count; i++)
			{
				Character other = Overlaps[i].GetComponentInParent<Character>();
				if (other == null || other == character || !ShouldStopFor(other))
					continue;

				// Not in the way when it is behind, which can happen with a long body.
				if (Vector3.Dot(other.transform.position - position, direction) > 0f)
					return true;
			}
			return false;
		}

		private static bool ShouldStopFor(Character other)
		{
			if (other.IsDead())
				return false;
			if (other.IsPlayer())
				return Plugin.StopForPlayers.Value && !other.InGhostMode() && !other.IsDebugFlying();
			return Plugin.StopForPets.Value && other.IsTamed();
		}

		private static void KeepBodyClear(Character character, State state, float time, float dt, ref Vector3 moveDir, Rigidbody body)
		{
			if (time >= state.NextScan)
			{
				state.NextScan = time + ScanInterval;
				Scan(character, state);
				TrackProgress(state, time);
			}

			if (state.ContactDir == Vector3.zero)
				return;

			// Stop walking into the obstacle, what remains of the movement slides along it.
			float inward = Vector3.Dot(moveDir, state.ContactDir);
			if (inward < 0f)
				moveDir -= state.ContactDir * inward;

			float depth = state.Remaining.magnitude;
			if (depth > DeadZone)
			{
				Vector3 step = state.ContactDir * Mathf.Min(depth, Plugin.PushSpeed.Value * dt);
				body.position += step;
				state.Remaining -= step;
			}
		}

		private static bool CanAdjust(Character character, Rigidbody body)
		{
			if (body.isKinematic || character.IsDead() || character.IsAttached() || character.HaveRider() || character.IsFlying())
				return false;
			BaseAI ai = character.GetBaseAI();
			return ai == null || !ai.IsSleeping();
		}

		private static State Measure(Character character, CapsuleCollider capsule)
		{
			var state = new State();
			Transform root = character.transform;
			Vector3 origin = root.position;
			bool found = false;
			float maxSide = 0f;
			float radius = capsule.radius * root.lossyScale.x;
			state.Radius = radius;

			// Measured from the skinned bones: they follow the actual model, whatever the creature,
			// including modded ones. Measured in world space so that the scale is accounted for.
			foreach (SkinnedMeshRenderer renderer in character.GetComponentsInChildren<SkinnedMeshRenderer>())
			{
				foreach (Transform bone in renderer.bones)
				{
					if (bone == null)
						continue;
					Vector3 offset = bone.position - origin;
					if (offset.magnitude > MaxBoneDistance)
						continue;

					float forward = Vector3.Dot(offset, root.forward);
					state.Front = Mathf.Max(state.Front, forward);
					state.Rear = Mathf.Min(state.Rear, forward);
					state.Top = Mathf.Max(state.Top, Vector3.Dot(offset, root.up));
					maxSide = Mathf.Max(maxSide, Mathf.Abs(Vector3.Dot(offset, root.right)));
					found = true;
				}
			}

			if (!found)
				return state;

			float margin = Plugin.BodyMargin.Value;
			float factor = Plugin.ClearanceFactor.Value;
			state.Front = (state.Front + margin) * factor;
			state.Rear = (state.Rear - margin) * factor;
			state.Top += margin;
			// Never wider than the capsule: any gap the creature fits through stays passable.
			state.HalfWidth = Mathf.Min((maxSide + margin) * factor, radius);
			// Stay above the floor and low steps.
			state.Bottom = Mathf.Max(0.35f, state.Top * 0.3f);
			state.Protected = state.Top > state.Bottom && (state.Front > radius || -state.Rear > radius);

			if (Plugin.LogBodySizes.Value)
			{
				Plugin.Logger.LogInfo($"{character.name}: front {state.Front:0.00}, rear {state.Rear:0.00}, half width {state.HalfWidth:0.00}, " +
					$"height {state.Bottom:0.00}-{state.Top:0.00}, capsule radius {radius:0.00}, protected: {state.Protected}");
			}
			return state;
		}

		private static void Scan(Character character, State state)
		{
			state.Remaining = Vector3.zero;
			state.ContactDir = Vector3.zero;
			if (_probe == null)
				return;

			Transform root = character.transform;
			// Yaw only: the box stays level whatever the ground tilt of the model.
			Vector3 flatForward = Vector3.ProjectOnPlane(root.forward, Vector3.up);
			if (flatForward.sqrMagnitude < 0.0001f)
				return;
			Quaternion rotation = Quaternion.LookRotation(flatForward);

			Vector3 center = root.position
				+ rotation * Vector3.forward * ((state.Front + state.Rear) * 0.5f)
				+ Vector3.up * ((state.Bottom + state.Top) * 0.5f);
			var size = new Vector3(state.HalfWidth * 2f, state.Top - state.Bottom, state.Front - state.Rear);

			int count = Physics.OverlapBoxNonAlloc(center, size * 0.5f, Overlaps, rotation, _obstacleMask, QueryTriggerInteraction.Ignore);
			if (count == 0)
				return;

			_probe.size = size;
			Vector3 correction = Vector3.zero;
			for (int i = 0; i < count; i++)
			{
				Collider obstacle = Overlaps[i];
				if (obstacle.transform.IsChildOf(root))
					continue;

				// Evaluated from the corrected position so that several colliders
				// forming the same wall are not counted several times.
				Transform other = obstacle.transform;
				if (!Physics.ComputePenetration(_probe, center + correction, rotation, obstacle, other.position, other.rotation, out Vector3 direction, out float distance))
					continue;
				if (Mathf.Abs(direction.y) > MaxVerticalNormal)
					continue;

				direction.y = 0f;
				correction += direction * distance;
			}

			if (correction == Vector3.zero)
				return;
			state.Remaining = Vector3.ClampMagnitude(correction, MaxCorrection);
			state.ContactDir = correction.normalized;
		}

		private static void TrackProgress(State state, float time)
		{
			float depth = state.Remaining.magnitude;
			if (depth <= DeadZone)
			{
				state.BestDepth = float.MaxValue;
				state.StuckTime = 0f;
				return;
			}

			if (depth < state.BestDepth - ProgressStep)
			{
				state.BestDepth = depth;
				state.StuckTime = 0f;
				return;
			}

			state.StuckTime += ScanInterval;
			if (state.StuckTime < GiveUpAfter)
				return;

			state.PauseUntil = time + PauseDuration;
			state.Remaining = Vector3.zero;
			state.ContactDir = Vector3.zero;
			state.BestDepth = float.MaxValue;
			state.StuckTime = 0f;
		}
	}
}
