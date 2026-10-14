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

        /// <summary>`double Get(object)` for a `double` field, with no boxing.</summary>
        public static Func<object, double> Double(FieldInfo field)
        {
            DynamicMethod method = NewMethod(typeof(double), field.Name, typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, double>)method.CreateDelegate(typeof(Func<object, double>));
        }

        /// <summary>
        /// `bool Get(object)` for a `bool` field, with no boxing.
        ///
        /// THE `float` ONE ABOVE WAS NOT ENOUGH, AND ITS ABSENCE WAS FOUND THE EXPENSIVE
        /// WAY. `World.PlayerDead` and `Character.SpiritMode` are `bool`, and a caller
        /// that had only the numeric accessors reached for `FieldInfo.GetValue` instead --
        /// which boxes on every read, on the frame path, for the exact reason this file
        /// exists. The frame guard reads two of them per frame.
        /// </summary>
        public static Func<object, bool> Bool(FieldInfo field)
        {
            DynamicMethod method = NewMethod(typeof(bool), field.Name, typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<object, bool>)method.CreateDelegate(typeof(Func<object, bool>));
        }

        /// <summary>
        /// `float Get()` for a `static float` field -- `World.WorldRotation`, which is
        /// `public static` (`World.cs:4212`) and is the camera's own rotation.
        ///
        /// Deliberately takes no argument, unlike every other accessor here: the field is
        /// static, so the caller has no object to pass and inventing a dummy parameter
        /// would let a caller pass the wrong one and still compile. The call sites wrap it
        /// with the object-shaped lambda they need, which makes the static-ness visible at
        /// the call site rather than hidden in a signature.
        /// </summary>
        public static Func<float> StaticFloat(FieldInfo field)
        {
            DynamicMethod method = NewMethod(typeof(float), field.Name);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<float>)method.CreateDelegate(typeof(Func<float>));
        }

        /// <summary>`void Set(object, bool)` for a `bool` field, with no boxing.</summary>
        public static Action<object, bool> BoolSetter(FieldInfo field)
        {
            DynamicMethod method = NewSetter(typeof(bool), field.Name, typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<object, bool>)method.CreateDelegate(typeof(Action<object, bool>));
        }

        /// <summary>
        /// `void Set(object, float)` for a `float` field, with no boxing.
        ///
        /// THE WRITE PATH IS WHERE THE PILOT LIVES. Phase 3's actuator is three of these
        /// per frame (the movement integrator's input), and `FieldInfo.SetValue` would box
        /// a `float` and build an `object[]` for each; on the game's own frame thread that
        /// is an allocation per frame per field, which is the cost PHASE2-SCOPE.md
        /// section 4.6 measured the hard way.
        ///
        /// This setter is also what makes the actuator possible for a *value type* field:
        /// `MoveAngle` is an inline `Angle` struct, and the receiver here is the boxed
        /// `Angle` itself, so `castclass` unboxes in place and `stfld` writes straight into
        /// it. No copy, no constructor call, no `GamePoint`.
        /// </summary>
        public static Action<object, float> FloatSetter(FieldInfo field)
        {
            DynamicMethod method = NewSetter(typeof(float), field.Name, typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<object, float>)method.CreateDelegate(typeof(Action<object, float>));
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

        /// <summary>
        /// The setter counterpart. `void`-returning and with a `void` first parameter slot,
        /// so the two helpers cannot be confused by a caller that reached for the wrong
        /// one: a getter has no second parameter and this one has no return.
        /// </summary>
        private static DynamicMethod NewSetter(Type valueType, string what, Type ownerType)
        {
            return new DynamicMethod("DrelaySet_" + what,
                                     typeof(void), new[] { ownerType, valueType },
                                     typeof(FieldAccessor).Module,
                                     skipVisibility: true);
        }
    }
}

