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
//
// =====================================================================================
// WHY EVERY NUMERIC ACCESSOR NOW CHECKS THE FIELD'S TYPE, AND WHAT THAT COST WHEN IT DID NOT
// =====================================================================================
//
// An emitted accessor is a `ldfld` plus a `ret`, and the JIT takes the RETURN TYPE at its word.
// For the shapes this project reads that is invisible -- a `float` field read into a `float`, a
// `bool` into a `bool` -- but where the two disagree the runtime does NOT throw. It either
// converts, or it reinterprets the bits, and the second one is a value that looks like a number:
//
//     a `double` field read by `Float`     -> 400      (RyuJIT inserts the narrowing; correct)
//     an `int` field read by `Double`      -> 2.08E-322 (the raw bits; 42 read as 42 gives this)
//     an `int` field read by `Float`       -> 5.9E-44
//
// The last two are the ones that bite: they are `> 0`, they print as `0` under a two-decimal
// format, and a comparison against a threshold silently changes meaning rather than failing.
//
// THIS IS NOT HYPOTHETICAL AND IT IS NOT NEW. The shipped client declares `Entity.Health` as a
// `double` and then, on `Player`, `public new int Health` -- so `Player.Health` is an INT that
// shadows a double, and both `Type.GetField("Health")` and `FieldLookup.Find` return the int.
// Every caller that built a `Double` accessor from it read 2.08E-322 per frame: the trace
// recorder's health column, the dodge snapshot's `Player.Health`, and the world log's `hp`. The
// 2026-10-18 collection run is the receipt -- `"hp":0` on all 292 frame lines, and the offline
// verifier passed because its fake game declared the field as the `double` the accessor wanted.
//
// So each accessor now demands a field it can actually read, and converts when the field is
// numeric but a different numeric type. A mismatch is an `ArgumentException` on the resolve
// callback -- where it is a named module that did not arm -- rather than a denormal on the frame
// path, where it is a decision made with a number nobody checked.

using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Drelay.Bootstrap
{
    public static class FieldAccessor
    {
        /// <summary>
        /// `float Get(object)` for a numeric field, with no boxing.
        ///
        /// The field may be any of the four numeric widths this project reads -- `float`,
        /// `double`, or one of the integer types -- and the conversion is emitted when they
        /// differ. A non-numeric field throws here rather than reading garbage on the frame
        /// path; see the header for the measurement that made that the rule.
        /// </summary>
        public static Func<object, float> Float(FieldInfo field)
        {
            DemandNumeric(field, "Float");
            DynamicMethod method = NewMethod(typeof(float), field.Name, typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            EmitReceiver(il, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            EmitNumericConversion(il, field.FieldType, typeof(float));
            il.Emit(OpCodes.Ret);
            return (Func<object, float>)method.CreateDelegate(typeof(Func<object, float>));
        }

        /// <summary>
        /// `int Get(object)` for a numeric field, with no boxing.
        ///
        /// THE CONVERSION MATTERS HERE TOO, and in the other direction: `CollisionObservation` and
        /// `TrajectorySampler` read `_startTime`/`AgeMs` for a clock subtraction, and a build that
        /// widened one of those to `long` would otherwise be read as the low four bytes of the
        /// value with no complaint.
        /// </summary>
        public static Func<object, int> Int(FieldInfo field)
        {
            DemandNumeric(field, "Int");
            DynamicMethod method = NewMethod(typeof(int), field.Name, typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            EmitReceiver(il, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            EmitNumericConversion(il, field.FieldType, typeof(int));
            il.Emit(OpCodes.Ret);
            return (Func<object, int>)method.CreateDelegate(typeof(Func<object, int>));
        }

        /// <summary>
        /// `double Get(object)` for a numeric field, with no boxing.
        ///
        /// THIS IS THE ONE THE SHIPPED BUILD BROKE. `Player.Health` is an `int` that shadows
        /// `Entity`'s `double`, so every caller that resolved it by name and built this accessor
        /// read a denormal -- and `WorldLog`, `DodgeAdapter`, `TraceRecorder` and `SteeringPilot`
        /// all did exactly that. The conversion below is the fix; the guard is why the next build
        /// that moves the field says so instead of quietly reading 2.08E-322.
        /// </summary>
        public static Func<object, double> Double(FieldInfo field)
        {
            DemandNumeric(field, "Double");
            DynamicMethod method = NewMethod(typeof(double), field.Name, typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            EmitReceiver(il, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            EmitNumericConversion(il, field.FieldType, typeof(double));
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
            Demand(field, typeof(bool), "Bool");
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
            DemandNumeric(field, "StaticFloat");
            DynamicMethod method = NewMethod(typeof(float), field.Name);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, field);
            EmitNumericConversion(il, field.FieldType, typeof(float));
            il.Emit(OpCodes.Ret);
            return (Func<float>)method.CreateDelegate(typeof(Func<float>));
        }

        /// <summary>`void Set(object, bool)` for a `bool` field, with no boxing.</summary>
        public static Action<object, bool> BoolSetter(FieldInfo field)
        {
            Demand(field, typeof(bool), "BoolSetter");
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
            Demand(field, typeof(float), "FloatSetter");
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
            DemandReference(field, "Reference");
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
            DemandReference(field, "Static");
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
            DemandNumeric(inner, "NestedFloat");
            DynamicMethod method = NewMethod(typeof(float), outer.Name + "." + inner.Name,
                                             typeof(object));
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            EmitReceiver(il, outer.DeclaringType);
            // ldflda gives the address of the inline struct inside the object, and the
            // second ldfld reads straight out of it -- no struct copy and no box.
            il.Emit(OpCodes.Ldflda, outer);
            il.Emit(OpCodes.Ldfld, inner);
            EmitNumericConversion(il, inner.FieldType, typeof(float));
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
            // A WRITE HAS NO CONVERSION TO FALL BACK ON. `stfld` must be given a value whose type
            // is the field's own, so this one demands exactly `float` rather than accepting a
            // numeric field the way the readers do.
            Demand(inner, typeof(float), "NestedFloatSetter");
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
        /// Refuses a field the accessor cannot read, naming the field and the accessor.
        ///
        /// THE MESSAGE IS THE PRODUCT. A caller that throws this has resolved the wrong member of a
        /// hierarchy -- `Player.Health` rather than `Entity.Health`, or a `byte` flag where a `bool`
        /// was expected -- and the two things it needs to know are which field it actually got and
        /// what the accessor wanted. That is why the declaring type is in the text: `Player.Health
        /// (Int32)` and `Entity.Health (Double)` are the same *name* and different fields, and it is
        /// the difference that made every live `hp` column read zero.
        /// </summary>
        private static void Demand(FieldInfo field, Type wanted, string accessor)
        {
            if (field == null)
            {
                throw new ArgumentNullException(nameof(field), accessor + " needs a field");
            }
            if (field.FieldType == wanted)
            {
                return;
            }
            throw new ArgumentException(
                accessor + " cannot read " + Describe(field) + ": the field is " + field.FieldType.Name
                + " and this accessor returns " + wanted.Name,
                nameof(field));
        }

        /// <summary>`Demand`, for the numeric accessors, which accept any numeric width.</summary>
        private static void DemandNumeric(FieldInfo field, string accessor)
        {
            if (field == null)
            {
                throw new ArgumentNullException(nameof(field), accessor + " needs a field");
            }
            if (IsNumeric(field.FieldType))
            {
                return;
            }
            throw new ArgumentException(
                accessor + " cannot read " + Describe(field) + ": the field is "
                + field.FieldType.Name + ", which is not numeric",
                nameof(field));
        }

        /// <summary>`Demand`, for the reference accessors, which cannot box a value type.</summary>
        private static void DemandReference(FieldInfo field, string accessor)
        {
            if (field == null)
            {
                throw new ArgumentNullException(nameof(field), accessor + " needs a field");
            }
            if (!field.FieldType.IsValueType)
            {
                return;
            }
            throw new ArgumentException(
                accessor + " cannot read " + Describe(field) + ": the field is "
                + field.FieldType.Name + ", a value type, and this accessor returns object",
                nameof(field));
        }

        private static string Describe(FieldInfo field)
        {
            return field.DeclaringType == null
                ? field.Name
                : field.DeclaringType.Name + "." + field.Name;
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(float) || type == typeof(double)
                || type == typeof(int) || type == typeof(uint)
                || type == typeof(long) || type == typeof(ulong)
                || type == typeof(short) || type == typeof(ushort)
                || type == typeof(byte) || type == typeof(sbyte);
        }

        /// <summary>
        /// The `conv.*` an accessor needs when the field's numeric type is not the one it returns.
        ///
        /// THIS IS WHAT REPLACES THE JIT'S SILENCE. `ldfld` pushes the field's own type and nothing
        /// in the IL requires it to match the method's return type, so without this an `int` read
        /// through a `double`-returning accessor is the int's bits reinterpreted -- 42 becomes
        /// 2.08E-322, a positive denormal that passes every `> 0` test and formats as `0`.
        ///
        /// `conv.r.un` handles the unsigned widths, whose conversion to a floating type is the one
        /// case `conv.r8` gets wrong (it would sign-extend `uint.MaxValue` to -1.0).
        /// </summary>
        private static void EmitNumericConversion(ILGenerator il, Type from, Type to)
        {
            if (from == to)
            {
                return;
            }
            if (from == typeof(uint) || from == typeof(ulong))
            {
                il.Emit(OpCodes.Conv_R_Un);
            }
            if (to == typeof(float))
            {
                il.Emit(OpCodes.Conv_R4);
            }
            else if (to == typeof(double))
            {
                il.Emit(OpCodes.Conv_R8);
            }
            else
            {
                il.Emit(OpCodes.Conv_I4);
            }
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

