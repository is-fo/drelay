// FieldLookup.cs -- find a field by any of several names, up the type hierarchy.
//
// WHY NAMES NEED A LIST.
//
// The hook cannot reference the game at compile time, so every field it reads is found by
// name -- and a name is a guess until it is checked against the shipped build.  Two things
// make one name not enough:
//
//   * the synthetic host and the real client spell things differently (`AgeMs` versus
//     `_startTime`, `Sin` versus `_angleSin`), and the same hook has to work against both,
//     because the host is how the sensor is tested;
//   * `Type.GetField` walks base classes for PUBLIC instance fields but not for private
//     ones, and the fields a projectile carries are spread across three types --
//     `Projectile`, `GameObject` and `Nodes.Node`.  `GameId` and `Radius` are declared on
//     `GameObject`, `Position` on `Node`, `_startTime` and `_angleSin` privately on
//     `Projectile`, and a lookup that only asks `Projectile` finds two of the six.
//
// That second point cost this project a wrong finding: a search restricted to the
// `Projectile` class concluded there was "no `GameId` field anywhere in the assembly",
// which was written into PHASE2-SCOPE.md section 4.9 before a search of the base classes
// found `GameObject.GameId` four thousand lines earlier.  A hierarchy walk is not
// defensive; it is the difference between an answer and a guess.
//
// WHICH NAME WAS FOUND IS RETURNED, NOT JUST THE FIELD.  The evidence has to say what the
// hook resolved, because "the field is missing" and "the field has a different name than
// the one this build looks for" are different problems with different owners, and only the
// name tells them apart.

using System;
using System.Reflection;

namespace Drelay.Bootstrap
{
    public static class FieldLookup
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>
        /// The first of <paramref name="candidates"/> that exists on <paramref name="type"/>
        /// or any base type below <see cref="object"/>.
        /// </summary>
        public static FieldInfo Find(Type type, out string foundName, params string[] candidates)
        {
            foundName = null;
            if (type == null || candidates == null)
            {
                return null;
            }

            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    // DeclaredOnly, because the walk already visits every level: without it
                    // the same inherited field would be reported as declared by the derived
                    // type, and the log would name the wrong owner.
                    FieldInfo field = current.GetField(candidates[i], Any | BindingFlags.DeclaredOnly);
                    if (field != null)
                    {
                        foundName = current.Name + "." + candidates[i];
                        return field;
                    }
                }
            }
            return null;
        }

        /// <summary>Convenience for a single name, keeping the call sites readable.</summary>
        public static FieldInfo Find(Type type, out string foundName, string name)
        {
            return Find(type, out foundName, new[] { name });
        }

        /// <summary>Where the type sits in its own hierarchy, for the evidence.</summary>
        public static string Hierarchy(Type type)
        {
            string text = "";
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                text = text.Length == 0 ? current.Name : text + " : " + current.Name;
            }
            return text.Length == 0 ? "(none)" : text;
        }
    }
}
