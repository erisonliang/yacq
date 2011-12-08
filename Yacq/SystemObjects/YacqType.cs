﻿// -*- mode: csharp; encoding: utf-8; tab-width: 4; c-basic-offset: 4; indent-tabs-mode: nil; -*-
// vim:set ft=cs fenc=utf-8 ts=4 sw=4 sts=4 et:
// $Id$
/* YACQ
 *   Yet Another Compilable Query Language, based on Expression Trees API
 * Copyright © 2011 Takeshi KIRIYA (aka takeshik) <takeshik@users.sf.net>
 * All rights reserved.
 * 
 * This file is part of YACQ.
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
 * THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using XSpect.Yacq.Expressions;

namespace XSpect.Yacq.SystemObjects
{
    /// <summary>
    /// Defines and creates new instances of types with YACQ codes during run time.
    /// </summary>
    public class YacqType
    {
        private readonly TypeBuilder _type;

        private readonly Type[] _typeArray;

        private readonly TypeBuilder _implType;

        private readonly MethodBuilder _prologue;

        private readonly List<MemberInfo> _members;

        private readonly Queue<Tuple<MethodBuilder, YacqExpression, Type>> _initializers;

        /// <summary>
        /// Initializes a new instance of the <see cref="YacqType"/> class.
        /// </summary>
        /// <param name="module">Target module to define new type.</param>
        /// <param name="name">The full path of the type. name cannot contain embedded nulls.</param>
        /// <param name="baseTypes">The list of the deriving type and interfaces that the type implements. The deriving type must be first in the list.</param>
        public YacqType(
            ModuleBuilder module,
            String name,
            params Type[] baseTypes
        )
        {
            this._type = module.DefineType(
                name,
                TypeAttributes.Public,
                baseTypes.Any()
                    ? baseTypes[0]
                    : typeof(Object),
                baseTypes.Skip(1).ToArray()
            );
            this._typeArray = new [] { this._type, };
            this._implType = this._type.DefineNestedType(":Impl", TypeAttributes.NestedPrivate)
                .Apply(t => t.SetCustomAttribute(new CustomAttributeBuilder(
                    typeof(CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes),
                    new Object[0]
                )));
            this._prologue = this._implType.DefineMethod(
                ":Prologue",
                MethodAttributes.Assembly | MethodAttributes.Static,
                typeof(void),
                this._typeArray
            );
            this._members = new List<MemberInfo>();
            this._initializers = new Queue<Tuple<MethodBuilder, YacqExpression, Type>>();
        }

        private FieldBuilder DefineField(
            String name,
            Type type,
            FieldAttributes attributes,
            YacqExpression initializer = null
        )
        {
            return this._type.DefineField(
                name,
                type,
                attributes
            )
                .If(_ => initializer != null, f =>
                    this._implType.DefineMethod(
                        GetName(f, "Init"),
                        MethodAttributes.Assembly | MethodAttributes.Static,
                        type,
                        this._typeArray
                    )
                    .Apply(
                        _ => this._prologue.GetILGenerator().Apply(
                            g => LoadArgs(g, 0, 0),
                            g => g.Emit(OpCodes.Call, _),
                            g => g.Emit(OpCodes.Stfld, f)
                        ),
                        _ => this.RequestInitializing(_, initializer, type, this._typeArray)
                    )
                )
                .Apply(this._members.Add);
        }

        /// <summary>
        /// Defines a new field to the type.
        /// </summary>
        /// <param name="name">The name of the field. <paramref name="name"/> cannot contain embedded nulls.</param>
        /// <param name="type">The type of the field/</param>
        /// <param name="initializer">The expression which is not reduced to be <see cref="LambdaExpression"/> for the initializer of the field, with a parameter for "this" instance, returns <paramref name="type"/> value.</param>
        /// <returns>The defined field.</returns>
        public FieldBuilder DefineField(
            String name,
            Type type,
            YacqExpression initializer = null
        )
        {
            return this.DefineField(
                name,
                type,
                FieldAttributes.Public,
                initializer
            );
        }

        private MethodBuilder DefineMethod(
            String name,
            MethodAttributes attributes,
            Type returnType,
            IList<Type> parameterTypes,
            YacqExpression body = null
        )
        {
            return this._type.DefineMethod(
                name,
                attributes,
                returnType,
                parameterTypes.ToArray()
            )
                .If(
                    m => body != null,
                    m => this._implType.DefineMethod(
                        GetName(m, "Impl"),
                        MethodAttributes.Assembly | MethodAttributes.Static,
                        returnType,
                        parameterTypes.StartWith(_type).ToArray()
                    )
                    .Apply(
                        _ => m.GetILGenerator().Apply(
                            g => LoadArgs(g, Enumerable.Range(0, parameterTypes.Count + 1)),
                            g => g.Emit(OpCodes.Call, _),
                            g => g.Emit(OpCodes.Ret)
                        ),
                        _ => RequestInitializing(_, body, returnType, parameterTypes.StartWith(_type).ToArray())
                    ),
                    m => m.GetILGenerator().Apply(
                        g =>
                        {
                            if (returnType.IsValueType)
                            {
                                switch (Type.GetTypeCode(returnType))
                                {
                                    case TypeCode.Byte:
                                    case TypeCode.SByte:
                                    case TypeCode.Char:
                                    case TypeCode.UInt16:
                                    case TypeCode.Int16:
                                    case TypeCode.UInt32:
                                    case TypeCode.Int32:
                                    case TypeCode.Boolean:
                                        g.Emit(OpCodes.Ldc_I4_0);
                                        break;
                                    case TypeCode.UInt64:
                                    case TypeCode.Int64:
                                        g.Emit(OpCodes.Ldc_I4_0);
                                        g.Emit(OpCodes.Conv_I8);
                                        break;
                                    case TypeCode.Single:
                                        g.Emit(OpCodes.Ldc_R4, (Single) 0);
                                        break;
                                    case TypeCode.Double:
                                        g.Emit(OpCodes.Ldc_R8, (Double) 0);
                                        break;
                                    default:
                                        if (returnType != typeof(void))
                                        {
                                            g.Emit(OpCodes.Ldloca_S, (Int16) 1);
                                            g.Emit(OpCodes.Initobj, returnType);
                                        }
                                        break;
                                }
                            }
                            else
                            {
                                g.Emit(OpCodes.Ldnull);
                            }
                            g.Emit(OpCodes.Ret);
                        }
                    )
                );
        }

        /// <summary>
        /// Defines a new method to the type.
        /// </summary>
        /// <param name="name">The name of the method. <paramref name="name"/> cannot contain embedded nulls.</param>
        /// <param name="returnType">The return type of the method.</param>
        /// <param name="parameterTypes">The types of the parameters of the method.</param>
        /// <param name="body">The expression which is not reduced to be <see cref="LambdaExpression"/> for the body of the method, with parameters for "this" instance and all method parameters, returns <paramref name="returnType"/> value.</param>
        /// <returns>The defined method.</returns>
        public MethodBuilder DefineMethod(
            String name,
            Type returnType,
            IList<Type> parameterTypes,
            YacqExpression body = null
        )
        {
            return this.DefineMethod(
                name,
                MethodAttributes.Public | MethodAttributes.HideBySig,
                returnType,
                parameterTypes,
                body
            );
        }

        private ConstructorBuilder DefineConstructor(
            MethodAttributes attributes,
            IList<Type> parameterTypes,
            YacqExpression body = null
        )
        {
            return this._type.DefineConstructor(
                attributes,
                CallingConventions.Standard,
                parameterTypes.ToArray()
            )
                .If(
                    c => body != null,
                    c => this._implType.DefineMethod(
                        GetName(c, "Impl"),
                        MethodAttributes.Assembly | MethodAttributes.Static,
                        typeof(void),
                        parameterTypes.StartWith(_type).ToArray()
                    )
                    .Apply(
                        _ => c.GetILGenerator().Apply(
                            g => LoadArgs(g, 0, 0),
                            g => g.Emit(OpCodes.Call, this._type.BaseType.GetConstructor(
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                null,
                                Type.EmptyTypes,
                                null
                            )),
                            g => g.Emit(OpCodes.Call, this._prologue),
                            g => LoadArgs(g, parameterTypes.Count),
                            g => g.Emit(OpCodes.Call, _),
                            g => g.Emit(OpCodes.Ret)
                        ),
                        _ => RequestInitializing(_, body, typeof(void), parameterTypes.StartWith(_type).ToArray())
                    ),
                    c => c.GetILGenerator().Apply(
                        g => LoadArgs(g, 0, 0),
                        g => g.Emit(OpCodes.Call, this._type.BaseType.GetConstructor(
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                            null,
                            Type.EmptyTypes,
                            null
                        )),
                        g => g.Emit(OpCodes.Call, this._prologue),
                        g => g.Emit(OpCodes.Ret)
                    )
                )
                .Apply(this._members.Add);
        }

        /// <summary>
        /// Defines a new constructor to the type.
        /// </summary>
        /// <param name="parameterTypes">The types of the parameters of the constructor.</param>
        /// <param name="body">The expression which is not reduced to be <see cref="LambdaExpression"/> for the body of the constructor, with parameters for "this" instance and all method parameters, returns no value.</param>
        /// <returns>The defined constructor.</returns>
        public ConstructorBuilder DefineConstructor(
            IList<Type> parameterTypes,
            YacqExpression body = null
        )
        {
            return this.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                parameterTypes,
                body
            );
        }

        private PropertyBuilder DefineProperty(
            String name,
            PropertyAttributes attributes,
            Type type,
            YacqExpression initializer = null,
            YacqExpression getter = null,
            YacqExpression setter = null
        )
        {
            return this._type.DefineProperty(name, attributes, type, Type.EmptyTypes)
                .Apply(p =>
                    (getter == null || setter == null
                        ? this.DefineField(
                              GetName(p, "Field"),
                              type,
                              FieldAttributes.Private,
                              initializer
                          )
                          .Apply(f => f.SetCustomAttribute(new CustomAttributeBuilder(
                              typeof(CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes),
                              new Object[0]
                          )))
                        : null
                    )
                    .Apply(
                        f => p.SetGetMethod(getter != null
                            ? this.DefineMethod(
                                  "get_" + name,
                                  MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                                  type,
                                  Type.EmptyTypes,
                                  getter
                              )
                            : this._type.DefineMethod(
                                  "get_" + name,
                                  MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                                  type,
                                  this._typeArray
                              )
                              .Apply(
                                  m => m.GetILGenerator().Apply(
                                      g => LoadArgs(g, 0),
                                      g => g.Emit(OpCodes.Ldfld, f),
                                      g => g.Emit(OpCodes.Ret)
                                  ),
                                  this._members.Add
                              )
                        ),
                        f => p.SetSetMethod(setter != null
                            ? this.DefineMethod(
                                  "set_" + name,
                                  MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                                  typeof(void),
                                  new [] { this._type, type, },
                                  setter
                              )
                            : this._type.DefineMethod(
                                  "set_" + name,
                                  MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                                  typeof(void),
                                  new [] { this._type, type, }
                              )
                              .Apply(
                                  m => m.GetILGenerator().Apply(
                                      g => LoadArgs(g, 0, 1),
                                      g => g.Emit(OpCodes.Stfld, f),
                                      g => g.Emit(OpCodes.Ret)
                                  ),
                                  this._members.Add
                              )
                        )
                    ),
                    this._members.Add
                );
        }

        /// <summary>
        /// Defines a new property to the type.
        /// </summary>
        /// <param name="name">The name of the property. <paramref name="name"/> cannot contain embedded nulls.</param>
        /// <param name="type">The type of the property.</param>
        /// <param name="initializer">The expression which is not reduced to be <see cref="LambdaExpression"/> for the initializer of the backing field, with a parameter for "this" instance, returns <paramref name="type"/> value.</param>
        /// <param name="getter">The expression which is not reduced to be <see cref="LambdaExpression"/> for the body of the getter of the property, with parameters for "this" instance, returns <paramref name="type"/> value, or <c>null</c> if the getter accesses to the backing field.</param>
        /// <param name="setter">The expression which is not reduced to be <see cref="LambdaExpression"/> for the body of the setter of the property, with parameters for "this" instance and <paramref name="type"/> value, returns no value, or <c>null</c> if the setter accesses to the backing field.</param>
        /// <returns>The defined property.</returns>
        public PropertyBuilder DefineProperty(
            String name,
            Type type,
            YacqExpression initializer = null,
            YacqExpression getter = null,
            YacqExpression setter = null
        )
        {
            return this.DefineProperty(
                name,
                PropertyAttributes.HasDefault,
                type,
                initializer,
                getter,
                setter
            );
        }

        /// <summary>
        /// Creates a <see cref="Type"/> object for the type. After defining members on the type, this method is called in order to load its Type object.
        /// </summary>
        /// <param name="symbols">The additional symbol table for reducing.</param>
        /// <returns>The new Type object for this type.</returns>
        public Type CreateType(SymbolTable symbols = null)
        {
            if (this.GetConstructors().IsEmpty())
            {
                this.DefineConstructor(Type.EmptyTypes);
            }
            return this._type.CreateType()
                .Apply(
                    t => this._initializers.ForEach(_ =>
                        _.Item2.Reduce(
                            new SymbolTable(symbols)
                            {
                                {"this", YacqExpression.TypeCandidate(t)},
                            },
                            _.Item3
                        )
                        .Apply(e => ((LambdaExpression) e).CompileToMethod(_.Item1))
                    ),
                    t => this._prologue.GetILGenerator().Emit(OpCodes.Ret),
                    t => this._implType.CreateType()
                );
        }

        /// <summary>
        /// Returns all the defined members in the type.
        /// </summary>
        /// <returns>A sequence of <see cref="MemberInfo"/> objects representing all the defined members of the type.</returns>
        public IEnumerable<MemberInfo> GetMembers()
        {
            return this._members.AsEnumerable();
        }

        /// <summary>
        /// Returns all the defined fields in the type.
        /// </summary>
        /// <returns>A sequence of <see cref="FieldBuilder"/> objects representing all the defined fields of the type.</returns>
        public IEnumerable<FieldBuilder> GetFields()
        {
            return this._members.OfType<FieldBuilder>();
        }

        /// <summary>
        /// Returns all the defined methods in the type.
        /// </summary>
        /// <returns>A sequence of <see cref="MethodBuilder"/> objects representing all the defined methods of the type.</returns>
        public IEnumerable<MethodBuilder> GetMethods()
        {
            return this._members.OfType<MethodBuilder>();
        }

        /// <summary>
        /// Returns all the defined constructors in the type.
        /// </summary>
        /// <returns>A sequence of <see cref="ConstructorBuilder"/> objects representing all the defined constructors of the type.</returns>
        public IEnumerable<ConstructorBuilder> GetConstructors()
        {
            return this._members.OfType<ConstructorBuilder>();
        }

        /// <summary>
        /// Returns all the defined properties in the type.
        /// </summary>
        /// <returns>A sequence of <see cref="PropertyBuilder"/> objects representing all the defined properties of the type.</returns>
        public IEnumerable<PropertyBuilder> GetProperties()
        {
            return this._members.OfType<PropertyBuilder>();
        }

        private void RequestInitializing(MethodBuilder method, YacqExpression expression, Type returnType, params Type[] parameterTypes)
        {
            this._initializers.Enqueue(Tuple.Create(
                method,
                expression,
                Expression.GetDelegateType(parameterTypes
                    .Concat(new [] { returnType, })
                    .ToArray()
                )
            ));
        }

        private static void LoadArgs(ILGenerator generator, IEnumerable<Int32> indexes)
        {
            indexes.ForEach(i =>
            {
                switch (i)
                {
                    case 0:
                        generator.Emit(OpCodes.Ldarg_0);
                        break;
                    case 1:
                        generator.Emit(OpCodes.Ldarg_1);
                        break;
                    case 2:
                        generator.Emit(OpCodes.Ldarg_2);
                        break;
                    case 3:
                        generator.Emit(OpCodes.Ldarg_3);
                        break;
                    default:
                        if (i <= Int16.MaxValue)
                        {
                            generator.Emit(OpCodes.Ldarg_S, (Int16) i);
                        }
                        else
                        {
                            generator.Emit(OpCodes.Ldarg, i);
                        }
                        break;
                }
            });
        }

        private static void LoadArgs(ILGenerator generator, params Int32[] indexes)
        {
            LoadArgs(generator, (IEnumerable<Int32>) indexes);
        }

        private static String GetName(MemberInfo member, String suffix)
        {
            return "<" + member.Name + ">:" + suffix;
        }
    }
}