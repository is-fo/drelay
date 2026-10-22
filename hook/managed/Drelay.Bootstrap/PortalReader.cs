// PortalReader.cs -- the lobby's portals, as the LOBBY PILOT's input, and the client's own
// `Portal.Activate(0)` as its output.
//
// =====================================================================================
// WHAT THIS IS FOR
// =====================================================================================
//
// `PLAN-AUTOFARM.md` section 5.2, built on the 2026-10-18 measurements: the lobby ("The Tavern")
// holds four portals inside a 30-unit square, the realm entrance is the one whose `Title` starts
// with a region tag (`[US] Cronus (42)`, `[EU] Ulysses (1)`), and ENTERING IS STANDING ON THE
// PORTAL'S OWN `Position` -- the client's `ActivatableObject.ActivateDistance` is 2 units, and the
// session's four walks ended 0.16, 0.44, 0.59 and 0.00 units from the portal's `Position`.
//
// The operator's first live farm session found the missing half by its absence: after a death the
// character stood in the lobby, because nothing ever asked it to walk to a portal. This file is the
// answer to "it should have reconnect": it makes the portals visible to the farmer, and it exposes
// the one client call that enters one.
//
// =====================================================================================
// IT IS A READER, NOT A POLICY
// =====================================================================================
//
// Like `EnemyReader`, this file finds the accessors, samples the frame's portals into the snapshot
// and says what it could not resolve. WHICH portal to walk to, when to walk and when to press it are
// the private farmer module's decisions (`PLAN-AUTOFARM.md` section 13), so this file contains no
// policy and publishes no goal.
//
// =====================================================================================
// THE TWO CALLS THAT MATTER, AND WHY THEY ARE NOT A PACKET
// =====================================================================================
//
// `Activate(0)` is what the input handler calls (`ObjectActivator`'s portal branch), so it runs the
// client's own gates -- locked, cooldown, the seeded-run cost alert. Section 5.2's preferred path is
// exactly this and section 5.2's fallback (`GameInput.Activate`, or `GmActivateObject` from the
// relay) exists only because the frame ordering of the field write was unverified. This file is the
// preferred path and nothing else.
//
// IT IS WARMED AT ARM TIME. The first call to `MethodInfo.Invoke` on a method the JIT has not seen
// compiles it, and that compilation would otherwise happen inside a patched frame -- the same
// argument `DodgeAdapter.Prepare` makes for every other frame-path method.

using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Drelay.Bootstrap
{
    public static class PortalReader
    {
        private const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        // --- Resolved once ------------------------------------------------------------------

        private static bool _resolved;
        private static string _unresolvedReason = "not attempted";
        private static string _status = "not attempted";

        private static Func<object> _getWorld;
        private static Func<object, object> _getActivatables;
        private static Type _portalType;
        private static Func<object, float> _getX;
        private static Func<object, float> _getY;
        private static Func<object, string> _getTitle;
        private static MethodInfo _activate;

        // --- Per-run counters ---------------------------------------------------------------
        private static long _portalsSeen;
        private static long _activationsAttempted;
        private static long _activationsFailed;

        /// <summary>Where the reader stands, for the arm-time log. One line, under 256 bytes.</summary>
        public static string Status()
        {
            return _status;
        }

        /// <summary>Why the reader is not available; null when it is.</summary>
        public static string UnresolvedReason
        {
            get { return _resolved ? null : _unresolvedReason; }
        }

        /// <summary>True when the reader resolved and the lobby pilot may ask for portals.</summary>
        public static bool Available => _resolved;

        /// <summary>This frame's portals, as the snapshot will carry them.</summary>
        public static long PortalsSeen => _portalsSeen;

        /// <summary>Activations attempted and how many the client's call threw or refused.</summary>
        public static long ActivationsAttempted => _activationsAttempted;
        public static long ActivationsFailed => _activationsFailed;

        /// <summary>Zeroes the per-run counters.</summary>
        public static void ResetCounters()
        {
            _portalsSeen = 0;
            _activationsAttempted = 0;
            _activationsFailed = 0;
        }

        // ===================================================================================
        // Resolve
        // ===================================================================================

        /// <summary>
        /// Finds `World.Activatables`, the `Portal` subclass, a portal's position and title, and
        /// `Activate(int)`.
        ///
        /// EVERY LOOKUP IS THE ONE THE 2026-10-18 SESSION PROVED, and the two that cost that session
        /// time are recorded here rather than re-learned:
        ///
        ///   * `World.Activatables` is a `List<ActivatableObject>`, so THE ELEMENT TYPE IS NOT THE
        ///     TYPE THAT CARRIES THE PORTAL'S FIELDS. `Title` is on `ActivatableObject` and the
        ///     position is `Node.Position`; resolving against the element type would find neither and
        ///     the whole feature would be silently empty.
        ///   * THE POSITION IS A `Vector3` FIELD ON `Node`, not a float `X` anywhere on a portal.
        ///     Grid units, the same ones the collision grid uses.
        /// </summary>
        public static bool Resolve(Assembly game, string worldTypeName)
        {
            try
            {
                if (game == null)
                {
                    _unresolvedReason = "the game assembly is not loaded";
                    _status = "portals: NOT available -- " + _unresolvedReason;
                    return false;
                }
                Type worldType = game.GetType(worldTypeName);
                FieldInfo activatablesField = worldType != null ? worldType.GetField("Activatables", Any) : null;
                if (activatablesField == null)
                {
                    _unresolvedReason = "World.Activatables not found";
                    _status = "portals: NOT available -- " + _unresolvedReason;
                    return false;
                }

                Type elementType = activatablesField.FieldType.IsGenericType
                    ? activatablesField.FieldType.GetGenericArguments()[0]
                    : null;
                _portalType = elementType != null ? FindPortalType(game, elementType) : null;
                if (_portalType == null)
                {
                    _unresolvedReason = "no Portal subclass of " +
                                        (elementType != null ? elementType.Name : "?") + " in the assembly";
                    _status = "portals: NOT available -- " + _unresolvedReason;
                    return false;
                }

                _getActivatables = FieldAccessor.Reference(activatablesField);
                _getX = VectorComponent(_portalType, "X", "Position", "ExactPosition");
                _getY = VectorComponent(_portalType, "Y", "Position", "ExactPosition");
                _getTitle = StringMember(_portalType, "Title");

                _activate = _portalType.GetMethod("Activate", BindingFlags.Public | BindingFlags.Instance,
                                                  null, new[] { typeof(int) }, null);
                if (_activate == null)
                {
                    _unresolvedReason = "Portal.Activate(int) not found on " + _portalType.Name;
                }
                else
                {
                    // WARM IT HERE, NOT INSIDE A PATCHED FRAME. See the file header.
                    try { RuntimeHelpers.PrepareMethod(_activate.MethodHandle); }
                    catch (Exception) { /* a method with a generic context refuses; leave it cold */ }
                }

                _resolved = _getX != null && _getY != null;
                _status = "portals: World.Activatables -> " + _portalType.Name
                          + " | x/y " + (_getX != null ? "Position" : "MISSING")
                          + " | title " + (_getTitle != null ? "Title" : "MISSING")
                          + " | enter " + (_activate != null ? "Activate(0)" : "MISSING");
                if (!_resolved)
                {
                    _unresolvedReason = "a portal's Position did not resolve";
                }
                return _resolved;
            }
            catch (Exception ex)
            {
                _unresolvedReason = ex.GetType().Name + ": " + ex.Message;
                _status = "portals: NOT available -- " + _unresolvedReason;
                _resolved = false;
                return false;
            }
        }

        /// <summary>`World.Shared`, resolved by the caller that already has the field.</summary>
        public static void ResolveWorld(FieldInfo sharedField)
        {
            _getWorld = sharedField != null ? FieldAccessor.Static(sharedField) : null;
        }

        // ===================================================================================
        // Fill
        // ===================================================================================

        /// <summary>
        /// Copies this frame's portals into <paramref name="s"/>, when the farmer is on.
        ///
        /// IT DOES NOTHING WHEN THE READER DID NOT RESOLVE OR THE FARMER IS OFF, so a run without the
        /// farmer is byte-identical to a build without this file: the snapshot's portal list stays
        /// what `Clear()` left it as -- empty.
        ///
        /// IT IS A LIST WALK, NOT A SPATIAL QUERY. `World.Activatables` is server-fed (the
        /// 2026-10-18 session measured portals arriving a thousand units away while the character
        /// stood still), so "find the portals" is "keep the ones the client has been told about",
        /// not "scan the map". The list is small -- four in the lobby -- and this runs only in a safe
        /// world, where the frame is nowhere near the budget.
        /// </summary>
        public static void Fill(DodgeSnapshot s, DodgeConfig config)
        {
            if (!_resolved || s == null || !config.FarmMode || !s.Player.SafeWorld || _getWorld == null)
            {
                return;
            }
            object world = _getWorld();
            if (world == null)
            {
                return;
            }
            object raw = _getActivatables(world);
            IList list = raw as IList;
            if (list == null)
            {
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                object item = list[i];
                if (item == null)
                {
                    continue;
                }
                // THE TYPE TEST IS THE SUBCLASS, because `Activatables` also holds the Vault
                // terminal, the shops and the chests (`PLAN-AUTOFARM.md` section 12.9 counts 40
                // activatable subclasses and 39 that are not portals). A non-portal has no
                // position-in-the-list semantics and no `Activate(int)` worth calling.
                if (!_portalType.IsInstanceOfType(item))
                {
                    continue;
                }
                _portalsSeen++;
                s.Portals.Add(new PortalTarget
                {
                    Object = item,
                    X = _getX(item),
                    Y = _getY(item),
                    Title = _getTitle != null ? _getTitle(item) : null,
                });
            }
        }

        // ===================================================================================
        // Enter
        // ===================================================================================

        /// <summary>
        /// Calls the client's own `Portal.Activate(0)` -- the function the input handler calls.
        ///
        /// IT RUNS THE CLIENT'S GATES AND THAT IS THE POINT: locked portals, the cooldown, and the
        /// seeded-run cost alert all live inside it, so this cannot enter a portal a player could not
        /// (`PLAN-AUTOFARM.md` section 5.2's first option, and the `World.RequestEscape` doctrine
        /// from `DODGE-ALGORITHM.md` section 7.4). `EnterPortal` (id 73) is deliberately not used:
        /// it is a dead enum slot with no implementing class.
        ///
        /// THE THROW IS CAUGHT AND COUNTED, because this runs inside a patched frame and an exception
        /// there is a crash, not a failed attempt.
        /// </summary>
        public static bool TryActivate(object portal, out string reason)
        {
            reason = null;
            if (portal == null) { reason = "no portal"; return false; }
            if (_activate == null) { reason = "Activate(int) never resolved"; return false; }
            _activationsAttempted++;
            try
            {
                _activate.Invoke(portal, new object[] { 0 });
                return true;
            }
            catch (Exception ex)
            {
                _activationsFailed++;
                reason = (ex.InnerException != null ? ex.InnerException.GetType().Name
                                                   : ex.GetType().Name)
                         + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
        }

        // ===================================================================================
        // Reflection helpers
        // ===================================================================================

        private static Type FindPortalType(Assembly game, Type elementType)
        {
            try
            {
                Type[] types = game.GetTypes();
                for (int i = 0; i < types.Length; i++)
                {
                    if (types[i].Name == "Portal" && elementType.IsAssignableFrom(types[i]))
                    {
                        return types[i];
                    }
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                Type[] loaded = ex.Types;
                if (loaded != null)
                {
                    for (int i = 0; i < loaded.Length; i++)
                    {
                        if (loaded[i] != null && loaded[i].Name == "Portal"
                            && elementType.IsAssignableFrom(loaded[i]))
                        {
                            return loaded[i];
                        }
                    }
                }
            }
            catch (Exception)
            {
                // fall through
            }
            return null;
        }

        /// <summary>
        /// One component of the first vector field found, up the hierarchy -- `Position` first, then
        /// `ExactPosition`. See the header for why it is not a float field called `X`.
        /// </summary>
        private static Func<object, float> VectorComponent(Type type, string component,
                                                          params string[] vectorFields)
        {
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                for (int i = 0; i < vectorFields.Length; i++)
                {
                    FieldInfo vector = t.GetField(vectorFields[i], Any | BindingFlags.DeclaredOnly);
                    if (vector == null)
                    {
                        continue;
                    }
                    FieldInfo part = vector.FieldType.GetField(component, Any);
                    if (part != null && part.FieldType == typeof(float))
                    {
                        return FieldAccessor.NestedFloat(vector, part);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// A string member by NAME, field first and property second, up the hierarchy.
        ///
        /// FIELD-FIRST IS THE FIX THIS PROJECT PAID FOR: `Title` is a public FIELD on
        /// `ActivatableObject`, and the world log's first version asked for a PROPERTY on `Portal`
        /// and wrote a null title on every row of a session.
        /// </summary>
        private static Func<object, string> StringMember(Type type, string name)
        {
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                FieldInfo field = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (field != null && field.FieldType == typeof(string))
                {
                    Func<object, object> read = FieldAccessor.Reference(field);
                    return o => read(o) as string;
                }
                PropertyInfo property = t.GetProperty(name, Any | BindingFlags.DeclaredOnly);
                if (property != null && property.PropertyType == typeof(string)
                    && property.GetGetMethod(true) != null)
                {
                    MethodInfo getter = property.GetGetMethod(true);
                    return o => (string)getter.Invoke(o, null);
                }
            }
            return null;
        }
    }
}
