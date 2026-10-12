// FieldAccessor.cs -- read a field of a type the hook cannot name, without boxing it.
//
// THE PROBLEM THIS SOLVES IS MEASURED, NOT ASSUMED.  `FieldInfo.GetValue` returns `object`,
// so reading a `float` or a `GamePoint` through it BOXES: a twenty-bullet frame with six
// fields each allocates about a hundred and twenty small objects, and the collections they
// trigger land in the frame's tail.  The Phase 2 session analyser measured exactly that --
// the read path's p99 sat inside the 150 microsecond frame-tick slice while the sampler's
// sat in the 150-300 bucket -- and the named cause was this boxing (PHASE2-SCOPE.md
// sections 4.7 and 4.11).
//
// WHY A DELEGATE RATHER THAN AN OFFSET.  A measured offset plus an unsafe pointer read is
// the smaller thing on paper, and it is the wrong tool here for two reasons.  The field
// layouts are CoreCLR's to choose -- HOOK-ARCHITECTURE.md section 5.5 says so, and says
// offsets must be measured rather than derived -- so an offset is a number that has to be
// re-measured for every type, on every run, and a wrong one is a wild read rather than an
// exception.  A delegate emitted once from the `FieldInfo` the resolver already found has
// neither problem: the JIT computes the offset from the field token, exactly as it would
// for a hand-written `obj.field`, and the read is a plain `ldfld` with no allocation and
// no pointer arithmetic of ours.
//
// The delegate is built once, on the resolve callback, and the frame path calls it.

using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Drelay.Bootstrap
{
    public static class FieldAccessor
    {
        /// <summary>`float Get(object)` for a `float` field, with no boxing.</summary>
        public static Func<object, float> Float(FieldInfo field)
        {
            DynamicMethod method = NewMethod(typeof(float), field.Name, typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, float>)method.CreateDelegate(typeof(Func<object, float>));
        }

        /// <summary>`int Get(object)` for an `int` field, with no boxing.</summary>
        public static Func<object, int> Int(FieldInfo field)
        {
            DynamicMethod method = NewMethod(typeof(int), field.Name, typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, int>)method.CreateDelegate(typeof(Func<object, int>));
        }

        /// <summary>`object Get(object)` for a reference field.</summary>
        public static Func<object, object> Reference(FieldInfo field)
        {
            DynamicMethod method = NewMethod(typeof(object), field.Name, typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, object>)method.CreateDelegate(typeof(Func<object, object>));
        }

        /// <summary>`object Get()` for a static reference field -- `World.Shared`, `DarzaWindow.Time`.</summary>
        public static Func<object> Static(FieldInfo field)
        {
            DynamicMethod method = NewMethod(typeof(object), field.Name);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object>)method.CreateDelegate(typeof(Func<object>));
        }

        /// <summary>
        /// `float Get(object)` for a `float` inside an inline value-type field -- the shape
        /// that matters most here, because a position is a `Vector3` (or a `GamePoint`) and
        /// reading the struct to get at it is what boxes.
        /// </summary>
        public static Func<object, float> NestedFloat(FieldInfo outer, FieldInfo inner)
        {
            DynamicMethod method = NewMethod(typeof(float), outer.Name + "." + inner.Name,
                                             typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, outer.DeclaringType);
            // ldflda gives the address of the inline struct inside the object, and the
            // second ldfld reads straight out of it -- no struct copy and no box.
            il.Emit(OpCodes.Ldflda, outer);
            il.Emit(OpCodes.Ldfld, inner);
            il.Emit(OpCodes.Ret);
            return (Func<object, float>)method.CreateDelegate(typeof(Func<object, float>));
        }

        private static DynamicMethod NewMethod(Type returnType, string what, params Type[] parameters)
        {
            // skipVisibility, because the fields this reads are private on the game's own
            // types -- `Projectile._angleSin` and `_startTime` are both private, and a
            // getter emitted without it would throw at invoke time rather than at build.
            return new DynamicMethod("DrelayField_" + what,
                                     returnType, parameters,
                                     typeof(FieldAccessor).Module,
                                     skipVisibility: true);
        }
    }
}

