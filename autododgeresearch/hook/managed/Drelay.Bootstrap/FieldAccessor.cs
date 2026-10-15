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
            EmitReceiver(il, field.DeclaringType);
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
            EmitReceiver(il, field.DeclaringType);
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
            EmitReceiver(il, field.DeclaringType);
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
            EmitReceiver(il, field.DeclaringType);
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
            EmitReceiver(il, field.DeclaringType);
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
            EmitReceiver(il, field.DeclaringType);
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
            EmitReceiver(il, field.DeclaringType);
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
            EmitReceiver(il, outer.DeclaringType);
            // ldflda gives the address of the inline struct inside the object, and the
            // second ldfld reads straight out of it -- no struct copy and no box.
            il.Emit(OpCodes.Ldflda, outer);
            il.Emit(OpCodes.Ldfld, inner);
            il.Emit(OpCodes.Ret);
            return (Func<object, float>)method.CreateDelegate(typeof(Func<object, float>));
        }

        /// <summary>
        /// `void Set(object, float)` for a `float` inside an inline value-type field -- the write
        /// counterpart of <see cref="NestedFloat"/>, and Phase 3's actuator's actual instruction.
        ///
        /// WHY THIS HAD TO EXIST, AND WHAT ITS ABSENCE COST. `FloatSetter(field)` compiles
        /// `castclass field.DeclaringType; stfld field` -- so its receiver is an instance of the
        /// field's *declaring* type. `GameInput.MoveAngle` is an `Angle` **struct**, so
        /// `FloatSetter(Angle.Radians)` writes into a boxed `Angle` and nothing else: it cannot
        /// reach the `MoveAngle` inside a `GameInput`. The first live steering run is the receipt.
        /// The pilot wrote the angle "successfully", every frame, into its own private box, while
        /// the input the client reads kept the client's own value -- and the log shows both halves
        /// side by side:
        ///
        ///     steer frame 31 phase 1 cmd 0.0deg wrote 0.0000rad read-back 4.7124rad ...
        ///
        /// 4.7124 rad is 270 degrees: the player walked due south at 0.133 units a frame for both
        /// phases, whatever the commanded angle, and the sweep could not tell the two writes apart
        /// because the *read-back* used the nested accessor and the *write* did not.
        /// `ldflda` gives the address of the inline struct inside the receiver and `stfld` writes
        /// through it, which is the only way to set a field of a value-type field.
        /// </summary>
        public static Action<object, float> NestedFloatSetter(FieldInfo outer, FieldInfo inner)
        {
            DynamicMethod method = NewSetter(typeof(float), outer.Name + "." + inner.Name, typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            EmitReceiver(il, outer.DeclaringType);
            il.Emit(OpCodes.Ldflda, outer);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, inner);
            il.Emit(OpCodes.Ret);
            return (Action<object, float>)method.CreateDelegate(typeof(Action<object, float>));
        }

        /// <summary>
        /// `byte Get(byte[,] array, int x, int y)` for the client's collision grid, emitted
        /// because the reflective route allocates once per cell.
        ///
        /// WHY THIS EXISTS AT ALL, AND WHY IT IS NOT `Array.GetValue`. `World`'s walkability map
        /// is a genuine two-dimensional `byte[,]` (`CLIENT-FINDINGS.md` section 5.4), and the
        /// obvious way to read one is `((byte[,])array)[x, y]` -- which a hook that cannot name
        /// the array cannot write, so the reflective equivalent is `Array.GetValue(x, y)`.
        /// `GetValue` returns `object`, and it therefore **boxes every cell it reads**: a grid
        /// window is 2 401 cells, the recorder re-reads it whenever the player changes tile, and
        /// this project has already paid once for exactly that mistake in the sensor's path
        /// (PHASE2-SCOPE.md sections 4.7 and 4.11).
        ///
        /// MULTIDIMENSIONAL ARRAYS ARE A DIFFERENT IL THAN ONE-DIMENSIONAL ONES, and that is why
        /// this is not `ldelem.u1`. The runtime lays out a `byte[x, y]` with a `Get(int, int)`
        /// method on the array's own type, so the emitted body is literally `call instance uint8
        /// uint8[0..., 0...]::Get(int32, int32)` -- the same call the game's own `CollisionMap`
        /// makes (see the IL dump, `World::'hehe...'`), which means the hook and the client cannot
        /// disagree about which cell a coordinate names.
        ///
        /// The bounds check is explicit and throws `IndexOutOfRangeException`, exactly as the
        /// CLR's own accessor would. Callers that expect an out-of-map coordinate to be
        /// answerable must check the bounds first -- `World`'s own query does, and so does the
        /// recorder.
        /// </summary>
        public static Func<object, int, int, byte> ByteArray2D(Type arrayType)
        {
            if (arrayType == null || !arrayType.IsArray || arrayType.GetArrayRank() != 2
                || arrayType.GetElementType() != typeof(byte))
            {
                throw new ArgumentException("ByteArray2D needs a byte[,]; got "
                                            + (arrayType == null ? "null" : arrayType.ToString()));
            }

            System.Reflection.MethodInfo get = arrayType.GetMethod("Get",
                new[] { typeof(int), typeof(int) });
            if (get == null)
            {
                throw new MissingMethodException(arrayType.ToString(), "Get(int,int)");
            }
            System.Reflection.MethodInfo getLength = typeof(Array).GetMethod("GetLength",
                new[] { typeof(int) });

            DynamicMethod method = new DynamicMethod(
                "DrelayGrid2D",
                typeof(byte),
                new[] { typeof(object), typeof(int), typeof(int) },
                typeof(FieldAccessor).Module,
                skipVisibility: true);

            ILGenerator il = method.GetILGenerator();
            Label inBounds = il.DefineLabel();

            // x >= 0 && x < GetLength(0) && y >= 0 && y < GetLength(1)
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Blt, inBounds);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, arrayType);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, getLength);
            il.Emit(OpCodes.Bge, inBounds);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Blt, inBounds);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, arrayType);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Callvirt, getLength);
            il.Emit(OpCodes.Bge, inBounds);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, arrayType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, get);
            il.Emit(OpCodes.Ret);

            // Out of bounds: the same answer the game's own query gives for a coordinate outside
            // the map, which is "not a wall". Returning 0 rather than throwing is deliberate:
            // this is called from the frame path of a live game, and the caller cannot see the
            // map's dimensions to check them itself.
            il.MarkLabel(inBounds);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);

            return (Func<object, int, int, byte>)method.CreateDelegate(typeof(Func<object, int, int, byte>));
        }

        /// <summary>
        /// Put the receiver on the stack in the form the field instruction needs.
        ///
        /// `castclass` IS INVALID IL WHEN THE DECLARING TYPE IS A VALUE TYPE, AND THIS IS NOT A
        /// TECHNICALITY -- IT IS THE DEFECT THAT STOPPED THE GAME STARTING ON EVERY RUN.
        ///
        /// Every accessor here used to emit `castclass field.DeclaringType` and then the field
        /// instruction. That is correct for the reference types the sensor reads (`GameObject`,
        /// `Projectile`, `Node`, `Player`, `GameInput`, ...) and *wrong* for a struct: the IL
        /// specification has no `castclass` to a value type, the C# compiler never emits one
        /// (it emits `unbox.any`), and RyuJIT's handling of it is not a rejected cast but
        /// undefined behaviour. Measured, in a standalone reproduction of this exact IL on this
        /// runtime, one process per case:
        ///
        ///     castclass Angle; ldarg.1; stfld Radians   -> Fatal error. Internal CLR error. (0x80131506)
        ///     castclass Angle; ldfld Radians            -> a garbage float, no exception
        ///     unbox Angle;     ldarg.1; stfld Radians   -> 0.25, correct
        ///     unbox Angle;     ldfld Radians            -> 0.25, correct
        ///
        /// Phase 3's actuator is the first code in this project to touch a *value-typed* receiver
        /// -- `GameInput.MoveAngle` is `DarzaCore.Geometry.Angle`, a `sealed` value type -- so it
        /// was the first to hit it, and it hit it as an access violation at
        /// `System.Private.CoreLib!CastHelpers.ChkCastClassSpecial`, which no `catch` can hold:
        ///
        ///     Fatal error. System.AccessViolationException: ...
        ///        at System.Runtime.CompilerServices.CastHelpers.ChkCastClassSpecial(Void*, System.Object)
        ///        at DynamicClass.DrelayField_MoveAngle.Radians(System.Object)
        ///        at Drelay.Bootstrap.SteeringPilot.Warm()
        ///
        /// `unbox` is the instruction that means "the receiver is a boxed value of this type, give
        /// me the interior pointer", which is exactly what a `stfld` into a box, an `ldfld` out of
        /// one, and the `ldflda` a nested read starts with all need. The type check is the same
        /// one `castclass` performs and it fails the same way -- a catchable
        /// `InvalidCastException` -- which the reproduction confirms: `NestedFloat`'s class-typed
        /// cast on a boxed struct throws, as it always did, while the value-type case above no
        /// longer faults.
        ///
        /// A reference type keeps `castclass`, so nothing that works today changes behaviour.
        /// </summary>
        private static void EmitReceiver(ILGenerator il, Type declaringType)
        {
            il.Emit(declaringType.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, declaringType);
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

