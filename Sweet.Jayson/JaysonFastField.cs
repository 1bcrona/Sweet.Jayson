﻿# region License
//	The MIT License (MIT)
//
//	Copyright (c) 2015, Cagatay Dogan
//
//	Permission is hereby granted, free of charge, to any person obtaining a copy
//	of this software and associated documentation files (the "Software"), to deal
//	in the Software without restriction, including without limitation the rights
//	to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//	copies of the Software, and to permit persons to whom the Software is
//	furnished to do so, subject to the following conditions:
//
//		The above copyright notice and this permission notice shall be included in
//		all copies or substantial portions of the Software.
//
//		THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//		IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//		FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//		AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//		LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//		OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
//		THE SOFTWARE.
# endregion License

using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Sweet.Jayson
{
	# region JaysonFastField

	internal sealed class JaysonFastField : IJaysonFastMember
	{
		private string m_Name;
		private string m_Alias;

		private bool m_Get;
		private bool m_Set;

		private Type m_MemberType;
		private FieldInfo m_FieldInfo;

		private bool m_CanRead;
		private bool m_CanWrite;

		private bool m_InvokeOnSet;

		private Func<object, object> m_GetDelegate;
		private Action<object, object> m_SetDelegate;

		public string Alias
		{
			get { return m_Alias; }
		}

		public string Name
		{
			get { return m_Name; }
		}

		public JaysonFastMemberType Type
		{
			get { return JaysonFastMemberType.Property; }
		}

		public Type MemberType
		{
			get { return m_MemberType; }
		}

		public bool CanRead
		{
			get { return m_CanRead; }
		}

		public bool CanWrite
		{
			get { return m_CanWrite || m_InvokeOnSet; }
		}

		public JaysonFastField(FieldInfo fi, bool initGet = true, bool initSet = true, 
			string alias = null)
		{
			m_Alias = alias;
			m_Name = fi.Name;
			m_FieldInfo = fi;
			m_MemberType = m_FieldInfo.FieldType;

			m_CanRead = true;
			m_CanWrite = !(m_FieldInfo.IsInitOnly || m_FieldInfo.IsStatic);
			m_InvokeOnSet = m_FieldInfo.IsInitOnly && !m_FieldInfo.IsStatic;

			if (initGet)
			{
				m_Get = true;
				InitializeGet(fi);
			}
			if (initSet)
			{
				m_Set = true;
				InitializeSet(fi);
			}
		}

		private void InitializeGet(FieldInfo fi)
		{
			if (m_CanRead) {
				var instance = Expression.Parameter (typeof(object), "instance");

				Type declaringT = fi.DeclaringType;
				UnaryExpression instanceCast = (!declaringT.IsValueType) ?
				Expression.TypeAs (instance, declaringT) : Expression.Convert (instance, declaringT);

				m_GetDelegate = Expression.Lambda<Func<object, object>> (Expression.TypeAs (Expression.Field (instanceCast,
					fi), typeof(object)), instance).Compile ();
			}
		}

		private void InitializeSet(FieldInfo fi)
		{
			if (m_CanWrite) {
				var instance = Expression.Parameter (typeof(object), "instance");
				var value = Expression.Parameter (typeof(object), "value");

				// value as T is slightly faster than (T)value, so if it's not a value type, use that
				Type declaringT = fi.DeclaringType;
				UnaryExpression instanceCast = (!declaringT.IsValueType) ?
				Expression.TypeAs (instance, declaringT) : Expression.Convert (instance, declaringT);

				UnaryExpression valueCast = (!fi.FieldType.IsValueType) ?
				Expression.TypeAs (value, fi.FieldType) : Expression.Convert (value, fi.FieldType);

				MemberExpression fieldExp = Expression.Field (instanceCast, fi);
				#if !(NET3500 || NET3000 || NET2000)
				BinaryExpression assignExp = Expression.Assign (fieldExp, valueCast);
				#else
				BinaryExpression assignExp = JaysonCommon.ExpressionAssign (fieldExp, valueCast);
				#endif

				m_SetDelegate = Expression.Lambda<Action<object, object>> (assignExp, instance, value).Compile ();
			}
		}

		public object Get(object instance)
		{
			if (m_CanRead)
			{
				if (!m_Get)
				{
					m_Get = true;
					InitializeGet(m_FieldInfo);
				}
				return m_GetDelegate(instance);
			}
			return null;
		}

		public void Set(object instance, object value)
		{
			if (m_CanWrite) {
				if (!m_Set) {
					m_Set = true;
					InitializeSet (m_FieldInfo);
				}
				if (m_SetDelegate != null) {
					m_SetDelegate (instance, value);
				}
			} else if (m_InvokeOnSet) {
				m_FieldInfo.SetValue (instance, value);
			}
		}
	}

	# endregion JaysonFastField
}