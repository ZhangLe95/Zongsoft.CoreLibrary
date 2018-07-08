﻿using System;
using System.Linq;
using System.ComponentModel;
using System.Threading;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;

namespace Zongsoft.Samples.DataEntity
{
	public static class DataEntity
	{
		#region 常量定义
		private const string ASSEMBLY_NAME = "Zongsoft.Dynamics.Entities";

		private const string PROPERTY_NAMES_VARIABLE = "$$PROPERTY_NAMES";
		private const string PROPERTY_TOKENS_VARIABLE = "$$PROPERTY_TOKENS";
		private const string MASK_VARIABLE = "$MASK$";
		#endregion

		#region 成员字段
		private static readonly MethodInfo POW_METHOD = typeof(Math).GetMethod("Pow", BindingFlags.Static | BindingFlags.Public);
		private static readonly ReaderWriterLockSlim _locker = new ReaderWriterLockSlim();
		private static readonly Dictionary<Type, Func<object>> _cache = new Dictionary<Type, Func<object>>();

		private static Type PROPERTY_TOKEN_TYPE = null;
		private static FieldInfo PROPERTY_TOKEN_GETTER_FIELD;
		private static FieldInfo PROPERTY_TOKEN_SETTER_FIELD;
		private static FieldInfo PROPERTY_TOKEN_ORDINAL_FIELD;
		private static ConstructorBuilder PROPERTY_TOKEN_CONSTRUCTOR;

		private static readonly AssemblyBuilder _assembly = AppDomain.CurrentDomain
			.DefineDynamicAssembly(new AssemblyName(ASSEMBLY_NAME), AssemblyBuilderAccess.RunAndSave);
		private static readonly ModuleBuilder _module = _assembly
			.DefineDynamicModule(ASSEMBLY_NAME, ASSEMBLY_NAME + ".dll");
		#endregion

		#region 公共方法
		public static T Build<T>() where T : Zongsoft.Data.IDataEntity
		{
			return (T)GetCreator<T>()();
		}

		public static IEnumerable<T> Build<T>(int count, Action<T, int> map = null) where T : Zongsoft.Data.IDataEntity
		{
			if(count < 1)
				throw new ArgumentOutOfRangeException(nameof(count));

			var creator = GetCreator<T>();

			if(map == null)
			{
				for(int i = 0; i < count; i++)
				{
					yield return (T)creator();
				}
			}
			else
			{
				for(int i = 0; i < count; i++)
				{
					var entity = (T)creator();
					map(entity, i);
					yield return entity;
				}
			}
		}
		#endregion

		#region 私有方法
		public static Func<object> GetCreator<T>()
		{
			var type = typeof(T);

			if(!type.IsInterface)
				throw new ArgumentException($"The '{type.FullName}' type must be an interface.");

			if(type.GetEvents().Length > 0)
				throw new ArgumentException($"The '{type.FullName}' interface cannot define any events.");

			if(type.GetMethods().Length > type.GetProperties().Length * 2)
				throw new ArgumentException($"The '{type.FullName}' interface cannot define any methods.");

			_locker.EnterReadLock();
			var existed = _cache.TryGetValue(type, out var creator);
			_locker.ExitReadLock();

			if(existed)
				return creator;

			try
			{
				_locker.EnterWriteLock();

				if(!_cache.TryGetValue(type, out creator))
					creator = _cache[type] = Compile(type);

				return creator;
			}
			finally
			{
				_locker.ExitWriteLock();
			}
		}

		public static void Save()
		{
			_assembly.Save(ASSEMBLY_NAME + ".dll");
		}

		private static Func<object> Compile(Type type)
		{
			ILGenerator generator;

			//如果是首次编译，则首先生成属性标记类型
			if(PROPERTY_TOKEN_TYPE == null)
				GeneratePropertyTokenClass();

			var builder = _module.DefineType(
				GetClassName(type) + "!",
				TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed);

			//添加类型的实现接口声明
			builder.AddInterfaceImplementation(type);
			builder.AddInterfaceImplementation(typeof(Zongsoft.Data.IDataEntity));

			//添加所有接口实现声明，并获取各接口的属性集，以及确认生成“INotifyPropertyChanged”接口
			var properties = MakeInterfaces(type, builder, out var propertyChanged);

			//生成所有接口定义的注解(自定义特性)
			GenerateAnnotations(builder, new HashSet<Type>());

			//生成构造函数
			GenerateConstructor(builder, properties.Count - properties.Count(p => !p.CanWrite), out var mask);

			//生成属性定义以及嵌套子类
			GenerateProperties(builder, mask, properties, propertyChanged, out var methods);

			//生成静态构造函数
			GenerateTypeInitializer(builder, properties, methods, out var names, out var tokens);

			//生成“HasChanges”方法
			GenerateHasChangesMethod(builder, mask, names, tokens);

			//生成“GetChanges”方法
			GenerateGetChangesMethod(builder, mask, names, tokens);

			//生成“TryGet”方法
			GenerateTryGetMethod(builder, mask, tokens);

			//生成“TrySet”方法
			GenerateTrySetMethod(builder, tokens);

			//构建类型
			type = builder.CreateType();

			//生成创建实例的动态方法
			var creator = new DynamicMethod("Create", typeof(object), Type.EmptyTypes);

			generator = creator.GetILGenerator();
			generator.Emit(OpCodes.Newobj, type.GetConstructor(Type.EmptyTypes));
			generator.Emit(OpCodes.Ret);

			//返回实例创建方法的委托
			return (Func<object>)creator.CreateDelegate(typeof(Func<object>));
		}

		private static void GenerateProperties(TypeBuilder builder, FieldBuilder mask, IList<PropertyInfo> properties, FieldBuilder propertyChangedField, out MethodToken[] methods)
		{
			//生成嵌套匿名委托静态类
			var nested = builder.DefineNestedType("!Methods!", TypeAttributes.NestedPrivate | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit);

			//创建返回参数（即方法标记）
			methods = new MethodToken[properties.Count];

			//定义只读属性的递增数量
			var countReadOnly = 0;
			//定义可写属性的总数量
			var countWritable = properties.Count - properties.Count(p => !p.CanWrite);

			//生成属性定义
			for(int i = 0; i < properties.Count; i++)
			{
				var field = properties[i].CanWrite ? builder.DefineField("$" + properties[i].Name, properties[i].PropertyType, FieldAttributes.Private) : null;
				var property = builder.DefineProperty(properties[i].Name, PropertyAttributes.None, properties[i].PropertyType, null);
				var attributes = properties[i].GetCustomAttributesData();

				var extensionAttribute = properties[i].GetCustomAttribute<PropertyExtensionAttribute>();

				//设置属性的自定义标签
				if(attributes != null && attributes.Count > 0)
				{
					foreach(var attribute in attributes)
					{
						var annotation = GetAnnotation(attribute);

						if(annotation != null)
							property.SetCustomAttribute(annotation);
					}
				}

				var getter = builder.DefineMethod("get_" + properties[i].Name, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot, properties[i].PropertyType, Type.EmptyTypes);
				var generator = getter.GetILGenerator();
				property.SetGetMethod(getter);

				if(extensionAttribute == null)
				{
					if(field == null)
					{
						generator.Emit(OpCodes.Newobj, typeof(NotSupportedException).GetConstructor(Type.EmptyTypes));
						generator.Emit(OpCodes.Throw);
					}
					else
					{
						generator.Emit(OpCodes.Ldarg_0);
						generator.Emit(OpCodes.Ldfld, field);
						generator.Emit(OpCodes.Ret);
					}
				}
				else
				{
					var method = extensionAttribute.Type.GetMethod("Get" + properties[i].Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new Type[] { properties[i].DeclaringType, properties[i].PropertyType }, null) ??
					             extensionAttribute.Type.GetMethod("Get" + properties[i].Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new Type[] { properties[i].DeclaringType }, null);

					if(method == null)
						throw new InvalidOperationException($"Not found the extension method of the {properties[i].Name} property in the {extensionAttribute.Type.FullName} extension type.");

					generator.Emit(OpCodes.Ldarg_0);

					if(method.GetParameters().Length == 2)
					{
						if(field == null)
							LoadDefaultValue(generator, properties[i].PropertyType);
						else
						{
							generator.Emit(OpCodes.Ldarg_0);
							generator.Emit(OpCodes.Ldfld, field);
						}
					}

					generator.Emit(OpCodes.Call, method);
					generator.Emit(OpCodes.Ret);
				}

				//生成获取属性字段的方法
				var getMethod = nested.DefineMethod("Get" + properties[i].Name,
					MethodAttributes.Assembly | MethodAttributes.Static, CallingConventions.Standard,
					typeof(object),
					new Type[] { property.DeclaringType });

				getMethod.DefineParameter(1, ParameterAttributes.None, "target");

				generator = getMethod.GetILGenerator();
				generator.Emit(OpCodes.Ldarg_0);
				//generator.Emit(OpCodes.Castclass, field.DeclaringType);
				if(field == null)
					generator.Emit(OpCodes.Callvirt, getter);
				else
					generator.Emit(OpCodes.Ldfld, field);
				if(properties[i].PropertyType.IsValueType)
					generator.Emit(OpCodes.Box, properties[i].PropertyType);
				generator.Emit(OpCodes.Ret);

				MethodBuilder setMethod = null;

				if(properties[i].CanWrite)
				{
					var setter = builder.DefineMethod("set_" + properties[i].Name, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot, null, new Type[] { properties[i].PropertyType });
					property.SetSetMethod(setter);

					setter.DefineParameter(1, ParameterAttributes.None, "value");
					generator = setter.GetILGenerator();
					var exit = generator.DefineLabel();

					if(extensionAttribute == null)
					{
						if(propertyChangedField != null)
						{
							//生成属性值是否发生改变的判断检测
							GeneratePropertyValueChangeChecker(generator, property, field, exit);
						}
					}
					else
					{
						var method = extensionAttribute.Type.GetMethod("Set" + properties[i].Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new Type[] { properties[i].DeclaringType, properties[i].PropertyType }, null);

						if(method == null)
						{
							if(propertyChangedField != null)
							{
								//生成属性值是否发生改变的判断检测
								GeneratePropertyValueChangeChecker(generator, property, field, exit);
							}
						}
						else
						{
							if(method.ReturnType != typeof(bool))
								throw new InvalidOperationException($"Invalid '{method}' extension method, it's return type must be boolean type.");

							generator.Emit(OpCodes.Ldarg_0);
							generator.Emit(OpCodes.Ldarg_1);
							generator.Emit(OpCodes.Call, method);
							generator.Emit(OpCodes.Brfalse_S, exit);
						}
					}

					generator.Emit(OpCodes.Ldarg_0);
					generator.Emit(OpCodes.Ldarg_1);
					generator.Emit(OpCodes.Stfld, field);

					if(countWritable <= 64)
						generator.Emit(OpCodes.Ldarg_0);

					generator.Emit(OpCodes.Ldarg_0);
					generator.Emit(OpCodes.Ldfld, mask);

					if(countWritable <= 8)
					{
						generator.Emit(OpCodes.Ldc_I4, (int)Math.Pow(2, i - countReadOnly));
						generator.Emit(OpCodes.Or);
						generator.Emit(OpCodes.Conv_U1);
						generator.Emit(OpCodes.Stfld, mask);
					}
					else if(countWritable <= 16)
					{
						generator.Emit(OpCodes.Ldc_I4, (int)Math.Pow(2, i - countReadOnly));
						generator.Emit(OpCodes.Or);
						generator.Emit(OpCodes.Conv_U2);
						generator.Emit(OpCodes.Stfld, mask);
					}
					else if(countWritable <= 32)
					{
						generator.Emit(OpCodes.Ldc_I4, (uint)Math.Pow(2, i - countReadOnly));
						generator.Emit(OpCodes.Or);
						generator.Emit(OpCodes.Conv_U4);
						generator.Emit(OpCodes.Stfld, mask);
					}
					else if(countWritable <= 64)
					{
						generator.Emit(OpCodes.Ldc_I8, (long)Math.Pow(2, i - countReadOnly));
						generator.Emit(OpCodes.Or);
						generator.Emit(OpCodes.Conv_U8);
						generator.Emit(OpCodes.Stfld, mask);
					}
					else
					{
						generator.Emit(OpCodes.Ldc_I4, (i - countReadOnly) / 8);
						generator.Emit(OpCodes.Ldelema, typeof(byte));
						generator.Emit(OpCodes.Dup);
						generator.Emit(OpCodes.Ldind_U1);

						switch((i - countReadOnly) % 8)
						{
							case 0:
								generator.Emit(OpCodes.Ldc_I4_1);
								break;
							case 1:
								generator.Emit(OpCodes.Ldc_I4_2);
								break;
							case 2:
								generator.Emit(OpCodes.Ldc_I4_4);
								break;
							case 3:
								generator.Emit(OpCodes.Ldc_I4_S, 8);
								break;
							case 4:
								generator.Emit(OpCodes.Ldc_I4_S, 16);
								break;
							case 5:
								generator.Emit(OpCodes.Ldc_I4_S, 32);
								break;
							case 6:
								generator.Emit(OpCodes.Ldc_I4_S, 64);
								break;
							case 7:
								generator.Emit(OpCodes.Ldc_I4_S, 128);
								break;
						}

						generator.Emit(OpCodes.Conv_U1);
						generator.Emit(OpCodes.Or);
						generator.Emit(OpCodes.Conv_U1);
						generator.Emit(OpCodes.Stind_I1);
					}

					//处理“PropertyChanged”事件
					if(propertyChangedField != null)
					{
						var RAISE_LABEL = generator.DefineLabel();

						// this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("xxx"));
						generator.Emit(OpCodes.Ldarg_0);
						generator.Emit(OpCodes.Ldfld, propertyChangedField);
						generator.Emit(OpCodes.Dup);
						generator.Emit(OpCodes.Brtrue_S, RAISE_LABEL);

						generator.Emit(OpCodes.Pop);
						generator.Emit(OpCodes.Ret);

						generator.MarkLabel(RAISE_LABEL);

						generator.Emit(OpCodes.Ldarg_0);
						generator.Emit(OpCodes.Ldstr, properties[i].Name);
						generator.Emit(OpCodes.Newobj, typeof(PropertyChangedEventArgs).GetConstructor(new Type[] { typeof(string) }));
						generator.Emit(OpCodes.Call, propertyChangedField.FieldType.GetMethod("Invoke"));
					}

					generator.MarkLabel(exit);
					generator.Emit(OpCodes.Ret);

					//生成设置属性的方法
					setMethod = nested.DefineMethod("Set" + properties[i].Name,
						MethodAttributes.Assembly | MethodAttributes.Static, CallingConventions.Standard,
						null,
						new Type[] { field.DeclaringType, typeof(object) });

					setMethod.DefineParameter(1, ParameterAttributes.None, "target");
					setMethod.DefineParameter(2, ParameterAttributes.None, "value");

					generator = setMethod.GetILGenerator();
					generator.Emit(OpCodes.Ldarg_0);
					//generator.Emit(OpCodes.Castclass, field.DeclaringType);
					generator.Emit(OpCodes.Ldarg_1);
					if(properties[i].PropertyType.IsValueType)
						generator.Emit(OpCodes.Unbox_Any, properties[i].PropertyType);
					else
						generator.Emit(OpCodes.Castclass, properties[i].PropertyType);
					generator.Emit(OpCodes.Call, setter);
					generator.Emit(OpCodes.Ret);
				}
				else
				{
					countReadOnly++;
				}

				//将委托方法保存到方法标记数组元素中
				methods[i] = new MethodToken(getMethod, setMethod);
			}

			//构建嵌套匿名静态类
			nested.CreateType();
		}

		private static CustomAttributeBuilder GetAnnotation(CustomAttributeData attribute)
		{
			var arguments = new object[attribute.ConstructorArguments.Count];

			if(arguments.Length > 0)
			{
				for(int i = 0; i < attribute.ConstructorArguments.Count; i++)
				{
					if(attribute.ConstructorArguments[i].Value == null)
						arguments[i] = null;
					else
					{
						if(Zongsoft.Common.TypeExtension.IsEnumerable(attribute.ConstructorArguments[i].Value.GetType()) &&
						   Zongsoft.Common.TypeExtension.GetElementType(attribute.ConstructorArguments[i].Value.GetType()) == typeof(CustomAttributeTypedArgument))
						{
							var args = new List<object>();

							foreach(CustomAttributeTypedArgument arg in (System.Collections.IEnumerable)attribute.ConstructorArguments[i].Value)
							{
								args.Add(arg.Value);
							}

							arguments[i] = args.ToArray();
						}
						else
							arguments[i] = attribute.ConstructorArguments[i].Value;
					}
				}
			}

			if(attribute.NamedArguments.Count == 0)
				return new CustomAttributeBuilder(attribute.Constructor, arguments);

			var properties = attribute.NamedArguments.Where(p => !p.IsField).ToArray();
			var fields = attribute.NamedArguments.Where(p => p.IsField).ToArray();

			return new CustomAttributeBuilder(attribute.Constructor, arguments,
			                                  properties.Select(p => (PropertyInfo)p.MemberInfo).ToArray(),
			                                  properties.Select(p => p.TypedValue.Value).ToArray(),
			                                  fields.Select(p => (FieldInfo)p.MemberInfo).ToArray(),
			                                  fields.Select(p => p.TypedValue.Value).ToArray());
		}

		private static void GenerateAnnotations(TypeBuilder builder, ISet<Type> types)
		{
			foreach(var type in builder.ImplementedInterfaces)
			{
				var attributes = type.GetCustomAttributesData();

				//设置接口的自定义标签
				if(attributes != null && attributes.Count > 0)
				{
					foreach(var attribute in attributes)
					{
						var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(attribute.AttributeType, typeof(AttributeUsageAttribute));

						if(usage != null && !usage.AllowMultiple && types.Contains(attribute.AttributeType))
							continue;

						var annotation = GetAnnotation(attribute);

						if(annotation != null)
						{
							builder.SetCustomAttribute(annotation);
							types.Add(attribute.AttributeType);
						}
					}
				}
			}
		}

		private static void GeneratePropertyValueChangeChecker(ILGenerator generator, PropertyBuilder property, FieldBuilder field, Label exit)
		{
			if(property.PropertyType.IsPrimitive)
			{
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldfld, field);
				generator.Emit(OpCodes.Ldarg_1);
				generator.Emit(OpCodes.Beq_S, exit);
			}
			else
			{
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldfld, field);

				if(property.PropertyType.IsValueType)
					generator.Emit(OpCodes.Box, property.PropertyType);

				generator.Emit(OpCodes.Ldarg_1);

				if(property.PropertyType.IsValueType)
					generator.Emit(OpCodes.Box, property.PropertyType);

				var equality = property.PropertyType.GetMethod("op_Equality", BindingFlags.Public | BindingFlags.Static) ??
				               typeof(object).GetMethod("Equals", BindingFlags.Public | BindingFlags.Static);

				generator.Emit(OpCodes.Call, equality);
				generator.Emit(OpCodes.Brtrue_S, exit);
			}
		}

		private static void GenerateTypeInitializer(TypeBuilder builder, IList<PropertyInfo> properties, MethodToken[] methods, out FieldBuilder names, out FieldBuilder tokens)
		{
			//var tokenType = typeof(PropertyToken<>).MakeGenericType(typeof(Zongsoft.Data.IDataEntity));
			//var tokenType = _module.GetType(builder.UnderlyingSystemType.FullName + "!PropertyToken");
			var tokenType = PROPERTY_TOKEN_TYPE;
			names = builder.DefineField(PROPERTY_NAMES_VARIABLE, typeof(string[]), FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
			tokens = builder.DefineField(PROPERTY_TOKENS_VARIABLE, typeof(Dictionary<,>).MakeGenericType(typeof(string), tokenType), FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
			var entityType = _module.GetType(builder.UnderlyingSystemType.FullName);

			//定义只读属性的递增数量
			var countReadOnly = 0;
			//定义可写属性的总数量
			var countWritable = properties.Count - properties.Count(p => !p.CanWrite);

			var generator = builder.DefineTypeInitializer().GetILGenerator();

			generator.Emit(OpCodes.Ldc_I4, countWritable);
			generator.Emit(OpCodes.Newarr, typeof(string));

			for(int i = 0; i < properties.Count; i++)
			{
				//忽略只读属性
				if(!properties[i].CanWrite)
				{
					countReadOnly++;
					continue;
				}

				generator.Emit(OpCodes.Dup);
				generator.Emit(OpCodes.Ldc_I4, i - countReadOnly);
				generator.Emit(OpCodes.Ldstr, properties[i].Name);
				generator.Emit(OpCodes.Stelem_Ref);
			}

			generator.Emit(OpCodes.Stsfld, names);

			//重置只读属性的递增量
			countReadOnly = 0;

			generator.Emit(OpCodes.Newobj, tokens.FieldType.GetConstructor(Type.EmptyTypes));

			for(int i = 0; i < properties.Count; i++)
			{
				generator.Emit(OpCodes.Dup);
				generator.Emit(OpCodes.Ldstr, properties[i].Name);
				generator.Emit(OpCodes.Ldc_I4, methods[i].SetMethod == null ? -1 : i - countReadOnly);

				generator.Emit(OpCodes.Ldnull);
				if(methods[i].GetMethod != null)
				{
					generator.Emit(OpCodes.Ldftn, methods[i].GetMethod);
					generator.Emit(OpCodes.Newobj, typeof(Func<,>).MakeGenericType(typeof(object), typeof(object)).GetConstructor(new Type[] { typeof(object), typeof(IntPtr) }));
				}

				generator.Emit(OpCodes.Ldnull);
				if(methods[i].SetMethod != null)
				{
					generator.Emit(OpCodes.Ldftn, methods[i].SetMethod);
					generator.Emit(OpCodes.Newobj, typeof(Action<,>).MakeGenericType(typeof(object), typeof(object)).GetConstructor(new Type[] { typeof(object), typeof(IntPtr) }));
				}
				else
				{
					countReadOnly++;
				}

				generator.Emit(OpCodes.Newobj, PROPERTY_TOKEN_CONSTRUCTOR);
				//generator.Emit(OpCodes.Newobj, tokenType.GetConstructor(new Type[] { typeof(int), typeof(Func<object, object>), typeof(Action<object, object>) }));
				generator.Emit(OpCodes.Call, tokens.FieldType.GetMethod("Add"));
			}

			generator.Emit(OpCodes.Stsfld, tokens);

			generator.Emit(OpCodes.Ret);
		}

		private static ConstructorBuilder GenerateConstructor(TypeBuilder builder, int count, out FieldBuilder mask)
		{
			mask = null;

			if(count <= 8)
				mask = builder.DefineField(MASK_VARIABLE, typeof(byte), FieldAttributes.Private);
			else if(count <= 16)
				mask = builder.DefineField(MASK_VARIABLE, typeof(UInt16), FieldAttributes.Private);
			else if(count <= 32)
				mask = builder.DefineField(MASK_VARIABLE, typeof(UInt32), FieldAttributes.Private);
			else if(count <= 64)
				mask = builder.DefineField(MASK_VARIABLE, typeof(UInt64), FieldAttributes.Private);
			else
				mask = builder.DefineField(MASK_VARIABLE, typeof(byte[]), FieldAttributes.Private);

			var constructor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, null);
			var generator = constructor.GetILGenerator();

			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));

			if(count > 64)
			{
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldc_I4, (int)Math.Ceiling(count / 8.0));
				generator.Emit(OpCodes.Newarr, typeof(byte));
				generator.Emit(OpCodes.Stfld, mask);
			}

			generator.Emit(OpCodes.Ret);

			return constructor;
		}

		private static void GenerateHasChangesMethod(TypeBuilder builder, FieldBuilder mask, FieldBuilder names, FieldBuilder tokens)
		{
			var method = builder.DefineMethod(typeof(Zongsoft.Data.IDataEntity).FullName + ".HasChanges",
				MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
				typeof(bool),
				new Type[] { typeof(string[]) });

			//添加方法的实现标记
			builder.DefineMethodOverride(method, typeof(Zongsoft.Data.IDataEntity).GetMethod("HasChanges"));

			//定义方法参数
			method.DefineParameter(1, ParameterAttributes.None, "names");

			//获取代码生成器
			var generator = method.GetILGenerator();

			generator.DeclareLocal(PROPERTY_TOKEN_TYPE);
			generator.DeclareLocal(typeof(int));

			var EXIT_LABEL = generator.DefineLabel();
			var MASKING_LABEL = generator.DefineLabel();
			var LOOP_INITIATE_LABEL = generator.DefineLabel();
			var LOOP_INCREASE_LABEL = generator.DefineLabel();
			var LOOP_BODY_LABEL = generator.DefineLabel();
			var LOOP_TEST_LABEL = generator.DefineLabel();

			// if(names==null || ...)
			generator.Emit(OpCodes.Ldarg_1);
			generator.Emit(OpCodes.Brfalse_S, MASKING_LABEL);

			// if(... || names.Length== 0)
			generator.Emit(OpCodes.Ldarg_1);
			generator.Emit(OpCodes.Ldlen);
			generator.Emit(OpCodes.Ldc_I4_0);
			generator.Emit(OpCodes.Ceq);
			generator.Emit(OpCodes.Brfalse_S, LOOP_INITIATE_LABEL);

			generator.MarkLabel(MASKING_LABEL);

			if(mask.FieldType.IsArray)
			{
				var INNER_LOOP_INCREASE_LABEL = generator.DefineLabel();
				var INNER_LOOP_BODY_LABEL = generator.DefineLabel();
				var INNER_LOOP_TEST_LABEL = generator.DefineLabel();

				// for(int i=0; ...; ...)
				generator.Emit(OpCodes.Ldc_I4_0);
				generator.Emit(OpCodes.Stloc_1, 0);
				generator.Emit(OpCodes.Br_S, INNER_LOOP_TEST_LABEL);

				generator.MarkLabel(INNER_LOOP_BODY_LABEL);

				// if(this.$MASK$[i] != 0)
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldfld, mask);
				generator.Emit(OpCodes.Ldloc_1);
				generator.Emit(OpCodes.Ldelem_U1);
				generator.Emit(OpCodes.Brfalse_S, INNER_LOOP_INCREASE_LABEL);

				// return true;
				generator.Emit(OpCodes.Ldc_I4_1);
				generator.Emit(OpCodes.Ret);

				generator.MarkLabel(INNER_LOOP_INCREASE_LABEL);

				// for(...; ...; i++)
				generator.Emit(OpCodes.Ldloc_1);
				generator.Emit(OpCodes.Ldc_I4_1);
				generator.Emit(OpCodes.Add);
				generator.Emit(OpCodes.Stloc_1);

				generator.MarkLabel(INNER_LOOP_TEST_LABEL);

				// for(...; i<this.$MASK$.Length; ...)
				generator.Emit(OpCodes.Ldloc_1);
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldfld, mask);
				generator.Emit(OpCodes.Ldlen);
				generator.Emit(OpCodes.Conv_I4);
				generator.Emit(OpCodes.Blt_S, INNER_LOOP_BODY_LABEL);

				// return false;
				generator.Emit(OpCodes.Ldc_I4_0);
				generator.Emit(OpCodes.Ret);
			}
			else
			{
				// return $MASK != 0;
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldfld, mask);
				generator.Emit(OpCodes.Ldc_I4_0);
				if(mask.FieldType == typeof(ulong))
					generator.Emit(OpCodes.Conv_I8);
				generator.Emit(OpCodes.Cgt_Un);
				generator.Emit(OpCodes.Ret);
			}

			generator.MarkLabel(LOOP_INITIATE_LABEL);

			// for(int i=0; ...; ...)
			generator.Emit(OpCodes.Ldc_I4_0);
			generator.Emit(OpCodes.Stloc_1, 0);
			generator.Emit(OpCodes.Br_S, LOOP_TEST_LABEL);

			generator.MarkLabel(LOOP_BODY_LABEL);

			// if($$PROPERTIES$$.TryGetValue(names[i], out property) && ...)
			generator.Emit(OpCodes.Ldsfld, tokens);
			generator.Emit(OpCodes.Ldarg_1);
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Ldelem_Ref);
			generator.Emit(OpCodes.Ldloca_S, 0);
			generator.Emit(OpCodes.Call, tokens.FieldType.GetMethod("TryGetValue", BindingFlags.Public | BindingFlags.Instance));
			generator.Emit(OpCodes.Brfalse_S, LOOP_INCREASE_LABEL);

			// if(... && property.Setter != null && ...)
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Ldfld, PROPERTY_TOKEN_SETTER_FIELD);
			generator.Emit(OpCodes.Brfalse_S, LOOP_INCREASE_LABEL);

			if(mask.FieldType.IsArray)
			{
				// if(... && (this.$MASK$[property.Ordinal / 8] >> (property.Ordinal % 8) & 1) == 1)
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldfld, mask);
				generator.Emit(OpCodes.Ldloc_0);
				generator.Emit(OpCodes.Ldfld, PROPERTY_TOKEN_ORDINAL_FIELD);
				generator.Emit(OpCodes.Ldc_I4_8);
				generator.Emit(OpCodes.Div);
				generator.Emit(OpCodes.Ldelem_U1);
				generator.Emit(OpCodes.Ldloc_0);
				generator.Emit(OpCodes.Ldfld, PROPERTY_TOKEN_ORDINAL_FIELD);
				generator.Emit(OpCodes.Ldc_I4_8);
				generator.Emit(OpCodes.Rem);
				generator.Emit(OpCodes.Shr);
				generator.Emit(OpCodes.Ldc_I4_1);
				generator.Emit(OpCodes.And);
				generator.Emit(OpCodes.Ldc_I4_1);
				generator.Emit(OpCodes.Bne_Un_S, LOOP_INCREASE_LABEL);
			}
			else
			{
				// if(... && (this.$MASK$ >> property.Ordinal & 1) == 1)
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldfld, mask);
				generator.Emit(OpCodes.Ldloc_0);
				generator.Emit(OpCodes.Ldfld, PROPERTY_TOKEN_ORDINAL_FIELD);
				generator.Emit(OpCodes.Shr_Un);
				generator.Emit(OpCodes.Ldc_I4_1);
				if(mask.FieldType == typeof(ulong))
					generator.Emit(OpCodes.Conv_I8);
				generator.Emit(OpCodes.And);
				generator.Emit(OpCodes.Ldc_I4_1);
				if(mask.FieldType == typeof(ulong))
					generator.Emit(OpCodes.Conv_I8);
				generator.Emit(OpCodes.Ceq);
				generator.Emit(OpCodes.Brfalse_S, LOOP_INCREASE_LABEL);
			}

			// return true;
			generator.Emit(OpCodes.Ldc_I4_1);
			generator.Emit(OpCodes.Ret);

			generator.MarkLabel(LOOP_INCREASE_LABEL);

			// for(...; ...; i++)
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Ldc_I4_1);
			generator.Emit(OpCodes.Add);
			generator.Emit(OpCodes.Stloc_1);

			generator.MarkLabel(LOOP_TEST_LABEL);

			// for(...; i<names.Length; ...)
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Ldarg_1);
			generator.Emit(OpCodes.Ldlen);
			generator.Emit(OpCodes.Conv_I4);
			generator.Emit(OpCodes.Blt_S, LOOP_BODY_LABEL);

			generator.MarkLabel(EXIT_LABEL);

			// return false;
			generator.Emit(OpCodes.Ldc_I4_0);
			generator.Emit(OpCodes.Ret);
		}

		private static void GenerateGetChangesMethod(TypeBuilder builder, FieldBuilder mask, FieldBuilder names, FieldBuilder tokens)
		{
			var method = builder.DefineMethod(typeof(Zongsoft.Data.IDataEntity).FullName + ".GetChanges",
				MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
				typeof(IDictionary<string, object>),
				Type.EmptyTypes);

			//添加方法的实现标记
			builder.DefineMethodOverride(method, typeof(Zongsoft.Data.IDataEntity).GetMethod("GetChanges"));

			//获取代码生成器
			var generator = method.GetILGenerator();

			generator.DeclareLocal(typeof(Dictionary<string, object>));
			generator.DeclareLocal(typeof(int));

			var EXIT_LABEL = generator.DefineLabel();
			var BEGIN_LABEL = generator.DefineLabel();
			var LOOP_INITIATE_LABEL = generator.DefineLabel();
			var LOOP_INCREASE_LABEL = generator.DefineLabel();
			var LOOP_BODY_LABEL = generator.DefineLabel();
			var LOOP_TEST_LABEL = generator.DefineLabel();

			if(!mask.FieldType.IsArray)
			{
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldfld, mask);
				generator.Emit(OpCodes.Brtrue_S, BEGIN_LABEL);

				generator.Emit(OpCodes.Ldnull);
				generator.Emit(OpCodes.Ret);
			}

			generator.MarkLabel(BEGIN_LABEL);

			// var dictioanry = new Dictionary<string, object>($$NAMES$$.Length);
			generator.Emit(OpCodes.Ldsfld, names);
			generator.Emit(OpCodes.Ldlen);
			generator.Emit(OpCodes.Conv_I4);
			generator.Emit(OpCodes.Newobj, typeof(Dictionary<string, object>).GetConstructor(new Type[] { typeof(int) }));
			generator.Emit(OpCodes.Stloc_0);

			generator.MarkLabel(LOOP_INITIATE_LABEL);

			// for(int i=0; ...; ...)
			generator.Emit(OpCodes.Ldc_I4_0);
			generator.Emit(OpCodes.Stloc_1);
			generator.Emit(OpCodes.Br_S, LOOP_TEST_LABEL);

			generator.MarkLabel(LOOP_BODY_LABEL);

			if(mask.FieldType.IsArray)
			{
				// if((this.$MASK$[i / 8] >> (i % 8) & 1) == 1)
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldfld, mask);
				generator.Emit(OpCodes.Ldloc_1);
				generator.Emit(OpCodes.Ldc_I4_8);
				generator.Emit(OpCodes.Div);
				generator.Emit(OpCodes.Ldelem_U1);
				generator.Emit(OpCodes.Ldloc_1);
				generator.Emit(OpCodes.Ldc_I4_8);
				generator.Emit(OpCodes.Rem);
				generator.Emit(OpCodes.Shr);
				generator.Emit(OpCodes.Ldc_I4_1);
				generator.Emit(OpCodes.And);
				generator.Emit(OpCodes.Ldc_I4_1);
				generator.Emit(OpCodes.Bne_Un_S, LOOP_INCREASE_LABEL);
			}
			else
			{
				// if((this.$MASK$ >> i) & i == 1)
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldfld, mask);
				generator.Emit(OpCodes.Ldloc_1);
				generator.Emit(OpCodes.Shr_Un);
				generator.Emit(OpCodes.Ldc_I4_1);
				if(mask.FieldType == typeof(ulong))
					generator.Emit(OpCodes.Conv_I8);
				generator.Emit(OpCodes.And);
				generator.Emit(OpCodes.Ldc_I4_1);
				if(mask.FieldType == typeof(ulong))
					generator.Emit(OpCodes.Conv_I8);
				generator.Emit(OpCodes.Bne_Un_S, LOOP_INCREASE_LABEL);
			}

			// dictioanry[$$NAMES[i]]
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Ldsfld, names);
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Ldelem_Ref);

			// $$PROPERTIES$$[$$NAMES$$[i]]
			generator.Emit(OpCodes.Ldsfld, tokens);
			generator.Emit(OpCodes.Ldsfld, names);
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Ldelem_Ref);
			generator.Emit(OpCodes.Call, tokens.FieldType.GetProperty("Item", new Type[] { typeof(string) }).GetMethod);

			generator.Emit(OpCodes.Ldfld, PROPERTY_TOKEN_GETTER_FIELD);
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Callvirt, PROPERTY_TOKEN_GETTER_FIELD.FieldType.GetMethod("Invoke"));
			generator.Emit(OpCodes.Callvirt, typeof(Dictionary<string, object>).GetProperty("Item", new Type[] { typeof(string) }).SetMethod);

			generator.MarkLabel(LOOP_INCREASE_LABEL);

			// for(...; ...; i++)
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Ldc_I4_1);
			generator.Emit(OpCodes.Add);
			generator.Emit(OpCodes.Stloc_1);

			generator.MarkLabel(LOOP_TEST_LABEL);

			// for(...; i<$NAMES$.Length; ...)
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Ldsfld, names);
			generator.Emit(OpCodes.Ldlen);
			generator.Emit(OpCodes.Conv_I4);
			generator.Emit(OpCodes.Blt_S, LOOP_BODY_LABEL);

			generator.MarkLabel(EXIT_LABEL);

			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Ret);
		}

		private static void GenerateTryGetMethod(TypeBuilder builder, FieldBuilder mask, FieldBuilder tokens)
		{
			var method = builder.DefineMethod(typeof(Zongsoft.Data.IDataEntity).FullName + ".TryGet",
				MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
				typeof(bool),
				new Type[] { typeof(string), typeof(object).MakeByRefType() });

			//添加方法的实现标记
			builder.DefineMethodOverride(method, typeof(Zongsoft.Data.IDataEntity).GetMethod("TryGet"));

			//定义方法参数
			method.DefineParameter(1, ParameterAttributes.None, "name");
			method.DefineParameter(2, ParameterAttributes.Out, "value");

			//获取代码生成器
			var generator = method.GetILGenerator();

			//声明本地变量
			generator.DeclareLocal(PROPERTY_TOKEN_TYPE);

			//定义代码标签
			var EXIT_LABEL = generator.DefineLabel();
			var GETBODY_LABEL = generator.DefineLabel();

			// value=null;
			generator.Emit(OpCodes.Ldarg_2);
			generator.Emit(OpCodes.Ldnull);
			generator.Emit(OpCodes.Stind_Ref);

			// if($$PROPERTIES.TryGet(name, out var property) && ...)
			generator.Emit(OpCodes.Ldsfld, tokens);
			generator.Emit(OpCodes.Ldarg_1);
			generator.Emit(OpCodes.Ldloca_S, 0);
			generator.Emit(OpCodes.Call, tokens.FieldType.GetMethod("TryGetValue", BindingFlags.Public | BindingFlags.Instance));
			generator.Emit(OpCodes.Brfalse_S, EXIT_LABEL);

			// if(... && (property.Ordinal<0 || ...))
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Ldfld, PROPERTY_TOKEN_ORDINAL_FIELD);
			generator.Emit(OpCodes.Ldc_I4_0);
			generator.Emit(OpCodes.Blt_S, GETBODY_LABEL);

			if(mask.FieldType.IsArray)
			{
				// if(... && (... || (this.$MASK$[property.Ordinal / 8] >> (property.Ordinal % 8) & 1) == 1))
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldfld, mask);
				generator.Emit(OpCodes.Ldloc_0);
				generator.Emit(OpCodes.Ldfld, PROPERTY_TOKEN_ORDINAL_FIELD);
				generator.Emit(OpCodes.Ldc_I4_8);
				generator.Emit(OpCodes.Div);
				generator.Emit(OpCodes.Ldelem_U1);
				generator.Emit(OpCodes.Ldloc_0);
				generator.Emit(OpCodes.Ldfld, PROPERTY_TOKEN_ORDINAL_FIELD);
				generator.Emit(OpCodes.Ldc_I4_8);
				generator.Emit(OpCodes.Rem);
				generator.Emit(OpCodes.Shr);
				generator.Emit(OpCodes.Ldc_I4_1);
				generator.Emit(OpCodes.And);
				generator.Emit(OpCodes.Ldc_I4_1);
				generator.Emit(OpCodes.Bne_Un_S, EXIT_LABEL);
			}
			else
			{
				// if(... && (... || ((this.$MASK$ >> property.Ordinal) & 1) == 1))
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Ldfld, mask);
				generator.Emit(OpCodes.Ldloc_0);
				generator.Emit(OpCodes.Ldfld, PROPERTY_TOKEN_ORDINAL_FIELD);
				generator.Emit(OpCodes.Shr_Un);
				generator.Emit(OpCodes.Ldc_I4_1);
				if(mask.FieldType == typeof(ulong))
					generator.Emit(OpCodes.Conv_I8);
				generator.Emit(OpCodes.And);
				generator.Emit(OpCodes.Ldc_I4_1);
				if(mask.FieldType == typeof(ulong))
					generator.Emit(OpCodes.Conv_I8);
				generator.Emit(OpCodes.Bne_Un_S, EXIT_LABEL);
			}

			generator.MarkLabel(GETBODY_LABEL);

			// value = property.Getter(this);
			generator.Emit(OpCodes.Ldarg_2);
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Ldfld, PROPERTY_TOKEN_GETTER_FIELD);
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Call, PROPERTY_TOKEN_GETTER_FIELD.FieldType.GetMethod("Invoke"));
			generator.Emit(OpCodes.Stind_Ref);

			// return true;
			generator.Emit(OpCodes.Ldc_I4_1);
			generator.Emit(OpCodes.Ret);

			generator.MarkLabel(EXIT_LABEL);

			//return false;
			generator.Emit(OpCodes.Ldc_I4_0);
			generator.Emit(OpCodes.Ret);
		}

		private static void GenerateTrySetMethod(TypeBuilder builder, FieldBuilder tokens)
		{
			var method = builder.DefineMethod(typeof(Zongsoft.Data.IDataEntity).FullName + ".TrySet",
				MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
				typeof(bool),
				new Type[] { typeof(string), typeof(object) });

			//添加方法的实现标记
			builder.DefineMethodOverride(method, typeof(Zongsoft.Data.IDataEntity).GetMethod("TrySet"));

			//定义方法参数
			method.DefineParameter(1, ParameterAttributes.None, "name");
			method.DefineParameter(2, ParameterAttributes.None, "value");

			//获取代码生成器
			var generator = method.GetILGenerator();

			//声明本地变量
			generator.DeclareLocal(PROPERTY_TOKEN_TYPE);

			//定义代码标签
			var EXIT_LABEL = generator.DefineLabel();

			// if($$PROPERTIES$$.TryGetValue(name, out var property))
			generator.Emit(OpCodes.Ldsfld, tokens);
			generator.Emit(OpCodes.Ldarg_1);
			generator.Emit(OpCodes.Ldloca_S, 0);
			generator.Emit(OpCodes.Call, tokens.FieldType.GetMethod("TryGetValue", BindingFlags.Public | BindingFlags.Instance));
			generator.Emit(OpCodes.Brfalse_S, EXIT_LABEL);

			// if(... && property.Setter != null)
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Ldfld, PROPERTY_TOKEN_SETTER_FIELD);
			generator.Emit(OpCodes.Brfalse_S, EXIT_LABEL);

			// property.Setter(this, value);
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Ldfld, PROPERTY_TOKEN_SETTER_FIELD);
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Ldarg_2);
			generator.Emit(OpCodes.Call, PROPERTY_TOKEN_SETTER_FIELD.FieldType.GetMethod("Invoke"));

			// return true;
			generator.Emit(OpCodes.Ldc_I4_1);
			generator.Emit(OpCodes.Ret);

			generator.MarkLabel(EXIT_LABEL);

			//return false;
			generator.Emit(OpCodes.Ldc_I4_0);
			generator.Emit(OpCodes.Ret);
		}

		private static void GeneratePropertyTokenClass()
		{
			var builder = _module.DefineType("<PropertyToken>", TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.SequentialLayout, typeof(ValueType));

			PROPERTY_TOKEN_ORDINAL_FIELD = builder.DefineField("Ordinal", typeof(int), FieldAttributes.Public | FieldAttributes.InitOnly);
			//PROPERTY_TOKEN_GETTER_FIELD = builder.DefineField("Getter", typeof(Func<,>).MakeGenericType(type, typeof(object)), FieldAttributes.Public | FieldAttributes.InitOnly);
			//PROPERTY_TOKEN_SETTER_FIELD = builder.DefineField("Setter", typeof(Action<,>).MakeGenericType(type, typeof(object)), FieldAttributes.Public | FieldAttributes.InitOnly);
			PROPERTY_TOKEN_GETTER_FIELD = builder.DefineField("Getter", typeof(Func<object,object>), FieldAttributes.Public | FieldAttributes.InitOnly);
			PROPERTY_TOKEN_SETTER_FIELD = builder.DefineField("Setter", typeof(Action<object,object>), FieldAttributes.Public | FieldAttributes.InitOnly);

			PROPERTY_TOKEN_CONSTRUCTOR = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, new Type[] { typeof(int), PROPERTY_TOKEN_GETTER_FIELD.FieldType, PROPERTY_TOKEN_SETTER_FIELD.FieldType });
			PROPERTY_TOKEN_CONSTRUCTOR.DefineParameter(1, ParameterAttributes.None, "ordinal");
			PROPERTY_TOKEN_CONSTRUCTOR.DefineParameter(2, ParameterAttributes.None, "getter");
			PROPERTY_TOKEN_CONSTRUCTOR.DefineParameter(3, ParameterAttributes.None, "setter");

			var generator = PROPERTY_TOKEN_CONSTRUCTOR.GetILGenerator();
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Ldarg_1);
			generator.Emit(OpCodes.Stfld, PROPERTY_TOKEN_ORDINAL_FIELD);
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Ldarg_2);
			generator.Emit(OpCodes.Stfld, PROPERTY_TOKEN_GETTER_FIELD);
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Ldarg_3);
			generator.Emit(OpCodes.Stfld, PROPERTY_TOKEN_SETTER_FIELD);
			generator.Emit(OpCodes.Ret);

			PROPERTY_TOKEN_TYPE = builder.CreateType();
		}

		private static FieldBuilder GeneratePropertyChangedEvent(TypeBuilder builder)
		{
			var exchangeMethod = typeof(Interlocked)
				.GetMethods(BindingFlags.Public | BindingFlags.Static)
				.First(p => p.Name == "CompareExchange" && p.IsGenericMethod)
				.MakeGenericMethod(typeof(PropertyChangedEventHandler));

			//添加类型的实现接口声明
			if(!builder.ImplementedInterfaces.Contains(typeof(INotifyPropertyChanged)))
				builder.AddInterfaceImplementation(typeof(INotifyPropertyChanged));

			//定义“PropertyChanged”事件的委托链字段
			var field = builder.DefineField("PropertyChanged", typeof(PropertyChangedEventHandler), FieldAttributes.Private);

			//定义“PropertyChanged”事件
			var e = builder.DefineEvent("PropertyChanged", EventAttributes.None, typeof(PropertyChangedEventHandler));

			//定义事件的Add方法
			var add = builder.DefineMethod("add_PropertyChanged", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.NewSlot, null, new Type[] { typeof(PropertyChangedEventHandler) });
			//定义事件方法的参数
			add.DefineParameter(1, ParameterAttributes.None, "value");

			var generator = add.GetILGenerator();
			generator.DeclareLocal(typeof(PropertyChangedEventHandler)); //original
			generator.DeclareLocal(typeof(PropertyChangedEventHandler)); //current
			generator.DeclareLocal(typeof(PropertyChangedEventHandler)); //latest

			var ADD_LOOP_LABEL = generator.DefineLabel();

			// var original = this.PropertyChanged;
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Ldfld, field);
			generator.Emit(OpCodes.Stloc_0);

			// do{}
			generator.MarkLabel(ADD_LOOP_LABEL);

			// current=original
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Stloc_1);

			// var latest=Delegate.Combine(current, value);
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Ldarg_1);
			generator.Emit(OpCodes.Call, typeof(Delegate).GetMethod("Combine", new Type[] { typeof(Delegate), typeof(Delegate) }));
			generator.Emit(OpCodes.Castclass, typeof(PropertyChangedEventHandler));
			generator.Emit(OpCodes.Stloc_2);

			// original = Interlocked.CompareExchange<PropertyChangedEventHandler>(ref this.PropertyChanged, latest, current);
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Ldflda, field);
			generator.Emit(OpCodes.Ldloc_2);
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Call, exchangeMethod);
			generator.Emit(OpCodes.Stloc_0);

			// while(original != current);
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Bne_Un_S, ADD_LOOP_LABEL);

			generator.Emit(OpCodes.Ret);

			//设置事件的Add方法
			e.SetAddOnMethod(add);

			//定义事件的Remove方法
			var remove = builder.DefineMethod("remove_PropertyChanged", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.NewSlot, null, new Type[] { typeof(PropertyChangedEventHandler) });
			//定义事件方法的参数
			remove.DefineParameter(1, ParameterAttributes.None, "value");

			generator = remove.GetILGenerator();
			generator.DeclareLocal(typeof(PropertyChangedEventHandler)); //original
			generator.DeclareLocal(typeof(PropertyChangedEventHandler)); //current
			generator.DeclareLocal(typeof(PropertyChangedEventHandler)); //latest

			var REMOVE_LOOP_LABEL = generator.DefineLabel();

			// var original = this.PropertyChanged;
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Ldfld, field);
			generator.Emit(OpCodes.Stloc_0);

			// do{}
			generator.MarkLabel(REMOVE_LOOP_LABEL);

			// current=original
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Stloc_1);

			// var latest=Delegate.Remove(current, value);
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Ldarg_1);
			generator.Emit(OpCodes.Call, typeof(Delegate).GetMethod("Remove", new Type[] { typeof(Delegate), typeof(Delegate) }));
			generator.Emit(OpCodes.Castclass, typeof(PropertyChangedEventHandler));
			generator.Emit(OpCodes.Stloc_2);

			// original = Interlocked.CompareExchange<PropertyChangedEventHandler>(ref this.PropertyChanged, latest, current);
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Ldflda, field);
			generator.Emit(OpCodes.Ldloc_2);
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Call, exchangeMethod);
			generator.Emit(OpCodes.Stloc_0);

			// while(original != current);
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Ldloc_1);
			generator.Emit(OpCodes.Bne_Un_S, REMOVE_LOOP_LABEL);

			generator.Emit(OpCodes.Ret);

			//设置事件的Remove方法
			e.SetRemoveOnMethod(remove);

			return field;
		}

		private static IList<PropertyInfo> MakeInterfaces(Type type, TypeBuilder builder, out FieldBuilder propertyChanged)
		{
			propertyChanged = null;
			var properties = new List<PropertyInfo>(type.GetProperties());
			var queue = new Queue<Type>(type.GetInterfaces());

			while(queue.Count > 0)
			{
				var interfaceType = queue.Dequeue();

				//如果该接口已经被声明，则跳过它
				if(builder.ImplementedInterfaces.Contains(interfaceType))
					continue;

				//将指定类型继承的接口加入到实现接口声明中
				builder.AddInterfaceImplementation(interfaceType);

				if(interfaceType == typeof(INotifyPropertyChanged))
				{
					if(propertyChanged == null)
						propertyChanged = GeneratePropertyChangedEvent(builder); //生成“INotifyPropertyChanged”接口实现
				}
				else
				{
					properties.AddRange(interfaceType.GetProperties());

					//获取当前接口的父接口
					var baseInterfaces = interfaceType.GetInterfaces();

					if(baseInterfaces != null && baseInterfaces.Length > 0)
					{
						foreach(var baseInterface in baseInterfaces)
						{
							queue.Enqueue(baseInterface);
						}
					}
				}
			}

			return properties;
		}

		private static void LoadDefaultValue(ILGenerator generator, Type type)
		{
			switch(Type.GetTypeCode(type))
			{
				case TypeCode.Boolean:
					generator.Emit(OpCodes.Ldc_I4_0);
					break;
				case TypeCode.Byte:
					generator.Emit(OpCodes.Ldc_I4_0);
					generator.Emit(OpCodes.Conv_U1);
					break;
				case TypeCode.SByte:
					generator.Emit(OpCodes.Ldc_I4_0);
					generator.Emit(OpCodes.Conv_I1);
					break;
				case TypeCode.Single:
					generator.Emit(OpCodes.Ldc_R4, 0);
					break;
				case TypeCode.Double:
					generator.Emit(OpCodes.Ldc_R8, 0);
					break;
				case TypeCode.Int16:
					generator.Emit(OpCodes.Ldc_I4_0);
					generator.Emit(OpCodes.Conv_I2);
					break;
				case TypeCode.Int32:
					generator.Emit(OpCodes.Ldc_I4_0);
					break;
				case TypeCode.Int64:
					generator.Emit(OpCodes.Ldc_I4_0);
					generator.Emit(OpCodes.Conv_I8);
					break;
				case TypeCode.UInt16:
					generator.Emit(OpCodes.Ldc_I4_0);
					generator.Emit(OpCodes.Conv_U2);
					break;
				case TypeCode.UInt32:
					generator.Emit(OpCodes.Ldc_I4_0);
					generator.Emit(OpCodes.Conv_U4);
					break;
				case TypeCode.UInt64:
					generator.Emit(OpCodes.Ldc_I4_0);
					generator.Emit(OpCodes.Conv_U8);
					break;
				case TypeCode.String:
					generator.Emit(OpCodes.Ldnull);
					break;
				case TypeCode.Char:
					generator.Emit(OpCodes.Ldsfld, typeof(Char).GetField("MinValue", BindingFlags.Public | BindingFlags.Static));
					break;
				case TypeCode.DateTime:
					generator.Emit(OpCodes.Ldsfld, typeof(DateTime).GetField("MinValue", BindingFlags.Public | BindingFlags.Static));
					break;
				case TypeCode.DBNull:
					generator.Emit(OpCodes.Ldsfld, typeof(DBNull).GetField("Value", BindingFlags.Public | BindingFlags.Static));
					break;
				case TypeCode.Decimal:
					generator.Emit(OpCodes.Ldsfld, typeof(Decimal).GetField("Zero", BindingFlags.Public | BindingFlags.Static));
					break;
			}

			if(type.IsValueType)
			{
				if(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
					generator.Emit(OpCodes.Ldnull);
				else
					generator.Emit(OpCodes.Newobj, type.GetConstructor(Type.EmptyTypes));
			}
			else
			{
				generator.Emit(OpCodes.Ldnull);
			}
		}

		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		private static string GetClassName(Type type)
		{
			return (string.IsNullOrEmpty(type.Namespace) ? string.Empty : type.Namespace + ".") +
			       (type.Name.Length > 1 && type.Name[0] == 'I' && char.IsUpper(type.Name[1]) ? type.Name.Substring(1) : type.Name);
		}
		#endregion

		#region 嵌套子类
		private struct MethodToken
		{
			public MethodToken(MethodBuilder getMethod, MethodBuilder setMethod)
			{
				this.GetMethod = getMethod;
				this.SetMethod = setMethod;
			}

			public readonly MethodBuilder GetMethod;
			public readonly MethodBuilder SetMethod;
		}
		#endregion
	}
}
