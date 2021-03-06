﻿/*
 * Authors:
 *   钟峰(Popeye Zhong) <9555843@qq.com>
 *
 * Copyright (C) 2017 Zongsoft Corporation <http://www.zongsoft.com>
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
using System.Collections.Generic;

namespace Zongsoft.Transitions
{
	public abstract class StateTransferBase<TState> : IStateTransfer where TState : State
	{
		#region 成员字段
		private bool _enabled;
		#endregion

		#region 构造函数
		protected StateTransferBase()
		{
			_enabled = true;
		}
		#endregion

		#region 公共属性
		public bool Enabled
		{
			get
			{
				return _enabled;
			}
			set
			{
				_enabled = value;
			}
		}
		#endregion

		#region 公共方法
		public virtual bool CanTransfer(StateContext<TState> context)
		{
			return this.Enabled && context != null && (!context.Origin.Equals(context.Destination));
		}

		public virtual void Transfer(StateContext<TState> context)
		{
			if(this.CanTransfer(context))
				this.OnTransfer(context);
		}
		#endregion

		#region 显式实现
		bool IStateTransfer.CanTransfer(StateContextBase context)
		{
			return this.CanTransfer(context as StateContext<TState>);
		}

		void IStateTransfer.Transfer(StateContextBase context)
		{
			this.Transfer(context as StateContext<TState>);
		}
		#endregion

		#region 抽象方法
		protected abstract void OnTransfer(StateContext<TState> context);
		#endregion
	}
}
