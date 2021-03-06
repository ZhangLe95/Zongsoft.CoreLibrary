﻿/*
 * Authors:
 *   钟峰(Popeye Zhong) <9555843@qq.com>
 *
 * Copyright (C) 2010-2018 Zongsoft Corporation <http://www.zongsoft.com>
 *
 * This file is part of Zongsoft.CoreLibrary.
 *
 * Zongsoft.CoreLibrary is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 *
 * Zongsoft.CoreLibrary is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * Lesser General Public License for more details.
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with Zongsoft.CoreLibrary; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA
 */

using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Zongsoft.Reflection
{
	public static class FieldInfoExtension
	{
		public static Func<object, object> GenerateGetter(this FieldInfo field)
		{
			if(field == null)
				throw new ArgumentNullException(nameof(field));

			var method = new DynamicMethod("__Get" + field.Name, typeof(object), new Type[] { typeof(object) }, field.DeclaringType, true);
			var generator = method.GetILGenerator();

			generator.DeclareLocal(field.DeclaringType);
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(OpCodes.Castclass, field.DeclaringType);
			generator.Emit(OpCodes.Ldfld, field);

			if(field.FieldType.IsValueType)
				generator.Emit(OpCodes.Box, field.FieldType);

			generator.Emit(OpCodes.Stloc_0);
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Ret);

			return (Func<object, object>)method.CreateDelegate(typeof(Func<object, object>));
		}
	}
}
