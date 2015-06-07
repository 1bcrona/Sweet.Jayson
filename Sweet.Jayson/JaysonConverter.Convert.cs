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
using System.Collections;
#if !(NET3500 || NET3000 || NET2000)
using System.Collections.Concurrent;
#endif
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Data;
#if !(NET3500 || NET3000 || NET2000)
using System.Dynamic;
#endif
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace Sweet.Jayson
{
	# region JsonConverter

	public static partial class JaysonConverter
	{
		# region EvaluatedDictionaryType

		private class EvaluatedDictionaryType
		{
			public bool AsDictionary;
			public bool AsReadOnly;
			public Type EvaluatedType;
		}

		# endregion EvaluatedDictionaryType

		# region EvaluatedListType

		private class EvaluatedListType
		{
			public bool AsList;
			public bool AsArray;
			public bool AsReadOnly;
			public Type EvaluatedType;
		}

		# endregion EvaluatedListType

		# region Static Members

		private static readonly Dictionary<Type, ConstructorInfo> s_CtorCache = new Dictionary<Type, ConstructorInfo>();
		private static readonly Dictionary<Type, Func<object[], object>> s_ActivatorCache = new Dictionary<Type, Func<object[], object>>();

		private static readonly Dictionary<Type, EvaluatedListType> s_EvaluatedListTypeCache = new Dictionary<Type, EvaluatedListType>();
		private static readonly Dictionary<Type, EvaluatedDictionaryType> s_EvaluatedDictionaryTypeCache = new Dictionary<Type, EvaluatedDictionaryType>();

		private static readonly Dictionary<Type, Type> s_GenericUnderlyingCache = new Dictionary<Type, Type>();

		private static readonly JaysonCtorParamMatcher DefaultMatcher = CtorParamMatcher;

		# endregion Static Members

		# region Convert

		private static Type GetGenericUnderlyingList(JaysonTypeInfo info)
		{
			Type result;
			if (!s_GenericUnderlyingCache.TryGetValue(info.Type, out result))
			{
				Type[] argTypes = info.GenericArguments;
				if (argTypes[0] == typeof(object))
				{
					result = typeof(List<object>);
				}
				else
				{
					result = typeof(List<>).MakeGenericType(argTypes);
				}

				s_GenericUnderlyingCache[info.Type] = result;
			}
			return result;
		}

		private static Type GetGenericUnderlyingDictionary(JaysonTypeInfo info)
		{
			Type result;
			if (!s_GenericUnderlyingCache.TryGetValue(info.Type, out result))
			{
				Type[] argTypes = info.GenericArguments;
				if (argTypes[0] == typeof(string) && argTypes[1] == typeof(object))
				{
					result = typeof(Dictionary<string, object>);
				}
				else
				{
					result = typeof(Dictionary<,>).MakeGenericType(argTypes);
				}

				s_GenericUnderlyingCache[info.Type] = result;
			}
			return result;
		}

		private static ConstructorInfo GetReadOnlyCollectionCtor(Type rocType)
		{
			ConstructorInfo ctor;
			if (!s_CtorCache.TryGetValue(rocType, out ctor))
			{
				Type[] argTypes = JaysonTypeInfo.GetTypeInfo(rocType).GenericArguments;

				rocType = typeof(ReadOnlyCollection<>).MakeGenericType(argTypes);
				ctor = rocType.GetConstructor(new Type[] { typeof(IList<>).MakeGenericType(argTypes) });

				s_CtorCache[rocType] = ctor;
			}
			return ctor;
		}

		private static Func<object[], object> GetReadOnlyCollectionActivator(Type rocType)
		{
			Func<object[], object> result;
			if (!s_ActivatorCache.TryGetValue(rocType, out result))
			{
				result = JaysonCommon.CreateActivator(GetReadOnlyCollectionCtor(rocType));
				s_ActivatorCache[rocType] = result;
			}
			return result;
		}

		#if !(NET4000 || NET3500 || NET3000 || NET2000)
		private static ConstructorInfo GetReadOnlyDictionaryCtor(Type rodType)
		{
			ConstructorInfo ctor;
			if (!s_CtorCache.TryGetValue(rodType, out ctor))
			{
				Type[] argTypes = JaysonTypeInfo.GetTypeInfo(rodType).GenericArguments;

				rodType = typeof(ReadOnlyDictionary<,>).MakeGenericType(argTypes);
				ctor = rodType.GetConstructor(new Type[] { typeof(IDictionary<,>).MakeGenericType(argTypes) });

				s_CtorCache[rodType] = ctor;
			}
			return ctor;
		}

		private static Func<object[], object> GetReadOnlyDictionaryActivator(Type rodType)
		{
			Func<object[], object> result;
			if (!s_ActivatorCache.TryGetValue(rodType, out result))
			{
				result = JaysonCommon.CreateActivator(GetReadOnlyDictionaryCtor(rodType));
				s_ActivatorCache[rodType] = result;
			}
			return result;
		}
		#endif

		# region Convert DataTable & DataSet

		private static void SetExtendedProperties(PropertyCollection extendedProperties, object obj,
			JaysonDeserializationContext context)
		{
			var dct = obj as IDictionary<string, object>;
			if (dct != null)
			{
				object kvListObj;
				if (dct.TryGetValue("$kv", out kvListObj))
				{
					var kvList = kvListObj as List<object>;
					if (kvList != null)
					{
						int count = kvList.Count;
						if (count > 0)
						{
							IDictionary<string, object> kvp;
							for (int i = 0; i < count; i++)
							{
								kvp = (IDictionary<string, object>)kvList[i];

								extendedProperties.Add(ConvertObject(kvp["$k"], typeof(object), context),
									ConvertObject(kvp["$v"], typeof(object), context));
							}
						}
					}
					return;
				}

				foreach (var ekvp in dct)
				{
					if (ekvp.Key != "$type")
					{
						extendedProperties.Add(ekvp.Key, ConvertObject(ekvp.Value, typeof(object), context));
					}
				}
			}
		}

		private static void SetDataTableProperties(IDictionary<string, object> obj, DataTable dataTable,
			JaysonDeserializationContext context)
		{
			object propValue;
			if (dataTable.ChildRelations.Count == 0 && dataTable.ParentRelations.Count == 0)
			{
				if (obj.TryGetValue("Cs", out propValue) || obj.TryGetValue("cs", out propValue))
				{
					if (propValue is bool)
					{
						dataTable.CaseSensitive = (bool)propValue;
					}
					else if (propValue is string)
					{
						dataTable.CaseSensitive = (string)propValue == "true";
					}
				}

				if (obj.TryGetValue("Lcl", out propValue) || obj.TryGetValue("lcl", out propValue))
				{
					dataTable.Locale = CultureInfo.GetCultureInfo((string)propValue);
				}
			}

			if (obj.TryGetValue("De", out propValue) || obj.TryGetValue("de", out propValue))
			{
				dataTable.DisplayExpression = (string)propValue;
			}

			if (obj.TryGetValue("Ns", out propValue) || obj.TryGetValue("ns", out propValue))
			{
				dataTable.Namespace = (string)propValue;
			}

			if (obj.TryGetValue("Pfx", out propValue) || obj.TryGetValue("pfx", out propValue))
			{
				dataTable.Prefix = (string)propValue;
			}

			if (obj.TryGetValue("Tn", out propValue) || obj.TryGetValue("tn", out propValue))
			{
				dataTable.TableName = (string)propValue;
			}

			if (obj.TryGetValue("Ep", out propValue) || obj.TryGetValue("ep", out propValue))
			{
				SetExtendedProperties(dataTable.ExtendedProperties, propValue, context);
			}
		}

		private static void SetDataTableColumns(IDictionary<string, object> obj, DataTable dataTable,
			JaysonDeserializationContext context)
		{
			object columnsObj;
			if (obj.TryGetValue("Cols", out columnsObj) || obj.TryGetValue("cols", out columnsObj))
			{
				var columnList = (IList)columnsObj;

				int ordinal;
				Type dataType;
				string expression;
				MappingType mappingType;
				Dictionary<string, object> columnInfo;

				object propValue;
				DataColumn column;
				string columnName;

				List<DataColumn> unordinalColumnList = new List<DataColumn>();
				DataColumn[] ordinalColumnList = new DataColumn[columnList.Count];

				var settings = context.Settings;

				foreach (var columnInfoObj in columnList)
				{
					columnInfo = (Dictionary<string, object>)columnInfoObj;

					columnName = null;
					if (columnInfo.TryGetValue("Cn", out propValue) || columnInfo.TryGetValue("cn", out propValue))
					{
						columnName = (string)propValue ?? String.Empty;
					}

					if (columnName != null && dataTable.Columns.Contains(columnName))
					{
						column = dataTable.Columns[columnName];
					}
					else
					{
						if ((columnInfo.TryGetValue("Dt", out propValue) ||
							columnInfo.TryGetValue("dt", out propValue)) && propValue != null)
						{
							dataType = JaysonCommon.GetType((string)propValue, settings.Binder);

							JaysonTypeOverride typeOverride = settings.GetTypeOverride(dataType);
							if (typeOverride != null)
							{
								Type bindToType = typeOverride.BindToType;
								if (bindToType != null)
								{
									dataType = bindToType;
								}
							}
						}
						else
						{
							dataType = typeof(object);
						}

						expression = null;
						if (columnInfo.TryGetValue("Exp", out propValue) || columnInfo.TryGetValue("exp", out propValue))
						{
							expression = (string)propValue;
						}

						if ((columnInfo.TryGetValue("Cm", out propValue) ||
							columnInfo.TryGetValue("cm", out propValue)) && propValue != null)
						{
							mappingType = (MappingType)JaysonEnumCache.Parse((string)propValue, typeof(MappingType));
						}
						else
						{
							mappingType = MappingType.Element;
						}

						column = new DataColumn(columnName, dataType, expression, mappingType);

						ordinal = -1;
						if (columnInfo.TryGetValue("Ord", out propValue) || columnInfo.TryGetValue("ord", out propValue))
						{
							if (propValue is int)
							{
								ordinal = (int)propValue;
							}
							else
								if (propValue is long)
								{
									ordinal = (int)((long)propValue);
								}
								else
									if (propValue is string)
									{
										ordinal = int.Parse((string)propValue, JaysonConstants.InvariantCulture);
									}
						}

						if (ordinal > -1)
						{
							ordinalColumnList[ordinal] = column;
						}
						else
						{
							unordinalColumnList.Add(column);
						}
					}

					if (columnInfo.TryGetValue("Ns", out propValue) || columnInfo.TryGetValue("ns", out propValue))
					{
						column.Namespace = (string)propValue;
					}

					if (columnInfo.TryGetValue("Adbn", out propValue) || columnInfo.TryGetValue("adbn", out propValue))
					{
						if (propValue is bool)
						{
							column.AllowDBNull = (bool)propValue;
						}
						else
							if (propValue is string)
							{
								column.AllowDBNull = (string)propValue == "true";
							}
					}

					if (columnInfo.TryGetValue("Ai", out propValue) || columnInfo.TryGetValue("ai", out propValue))
					{
						if (propValue is bool)
						{
							column.AutoIncrement = (bool)propValue;
						}
						else
							if (propValue is string)
							{
								column.AutoIncrement = (string)propValue == "true";
							}
					}

					if (columnInfo.TryGetValue("Aisd", out propValue) || columnInfo.TryGetValue("aisd", out propValue))
					{
						if (propValue is int)
						{
							column.AutoIncrementSeed = (long)((int)propValue);
						}
						else
							if (propValue is long)
							{
								column.AutoIncrementSeed = (long)propValue;
							}
							else
								if (propValue is string)
								{
									column.AutoIncrementSeed = long.Parse((string)propValue, JaysonConstants.InvariantCulture);
								}
					}

					if (columnInfo.TryGetValue("Aistp", out propValue) || columnInfo.TryGetValue("aistp", out propValue))
					{
						if (propValue is int)
						{
							column.AutoIncrementSeed = (long)((int)propValue);
						}
						else
							if (propValue is long)
							{
								column.AutoIncrementStep = (long)propValue;
							}
							else
								if (propValue is string)
								{
									column.AutoIncrementStep = long.Parse((string)propValue, JaysonConstants.InvariantCulture);
								}
					}

					if (columnInfo.TryGetValue("Ml", out propValue) || columnInfo.TryGetValue("ml", out propValue))
					{
						if (propValue is int)
						{
							column.MaxLength = (int)propValue;
						}
						else
							if (propValue is long)
							{
								column.MaxLength = (int)((long)propValue);
							}
							else
								if (propValue is string)
								{
									column.MaxLength = int.Parse((string)propValue, JaysonConstants.InvariantCulture);
								}
					}

					if (columnInfo.TryGetValue("Cap", out propValue) || columnInfo.TryGetValue("cap", out propValue))
					{
						column.Caption = (string)propValue;
					}

					if (columnInfo.TryGetValue("Pfx", out propValue) || columnInfo.TryGetValue("pfx", out propValue))
					{
						column.Prefix = (string)propValue;
					}

					if (columnInfo.TryGetValue("Ro", out propValue) || columnInfo.TryGetValue("ro", out propValue))
					{
						if (propValue is bool)
						{
							column.ReadOnly = (bool)propValue;
						}
						else
							if (propValue is string)
							{
								column.ReadOnly = (string)propValue == "true";
							}
					}

					if (columnInfo.TryGetValue("Uq", out propValue) || columnInfo.TryGetValue("uq", out propValue))
					{
						if (propValue is bool)
						{
							column.Unique = (bool)propValue;
						}
						else
							if (propValue is string)
							{
								column.Unique = (string)propValue == "true";
							}
					}

					if (columnInfo.TryGetValue("Ep", out propValue) || columnInfo.TryGetValue("ep", out propValue))
					{
						SetExtendedProperties(column.ExtendedProperties, propValue, context);
					}
				}

				if (unordinalColumnList.Count > 0)
				{
					int columnCount = ordinalColumnList.Length;
					int unordColPos = unordinalColumnList.Count - 1;

					for (int i = columnCount - 1; i > -1; i--)
					{
						if (ordinalColumnList[i] == null)
						{
							ordinalColumnList[i] = unordinalColumnList[unordColPos--];
						}
					}
				}

				foreach (var dataCol in ordinalColumnList)
				{
					if (dataCol != null)
					{
						dataTable.Columns.Add(dataCol);
					}
				}
			}
		}

		private static void SetDataTablePrimaryKey(IDictionary<string, object> obj, DataTable dataTable,
			JaysonDeserializationContext context)
		{
			object primaryKey;
			if ((obj.TryGetValue("Pk", out primaryKey) ||
				obj.TryGetValue("pk", out primaryKey)) && (primaryKey != null))
			{
				var currPrimaryKey = dataTable.PrimaryKey;
				if (currPrimaryKey == null || currPrimaryKey.Length == 0)
				{
					var primaryKeyList = (IList)primaryKey;
					if (primaryKeyList.Count > 0)
					{
						string columnName;
						var columns = dataTable.Columns;
						List<DataColumn> primaryKeyColumns = new List<DataColumn>();

						foreach (var column in primaryKeyList)
						{
							columnName = (string)column;
							if (columnName != null && columns.Contains(columnName))
							{
								primaryKeyColumns.Add(columns[columnName]);
							}
						}

						if (primaryKeyColumns.Count > 0)
						{
							dataTable.PrimaryKey = primaryKeyColumns.ToArray();
						}
					}
				}
			}
		}

		private static void SetDataTableRows(IDictionary<string, object> obj, DataTable dataTable,
			JaysonDeserializationContext context)
		{
			object rowsObj;
			if (obj.TryGetValue("Rows", out rowsObj))
			{
				var rowsList = (IList)rowsObj;
				if (rowsList.Count > 0)
				{
					dataTable.BeginLoadData();
					try
					{
						int columnCount = dataTable.Columns.Count;
						Type[] columnTypes = new Type[columnCount];

						for (int i = 0; i < columnCount; i++)
						{
							columnTypes[i] = dataTable.Columns[i].DataType;
						}

						IList rowList;
						int itemCount;
						object[] items;
						Type columnType;
						object rowValue;

						foreach (var row in rowsList)
						{
							rowList = (IList)row;

							itemCount = rowList.Count;
							items = new object[itemCount];

							for (int i = 0; i < itemCount; i++)
							{
								rowValue = rowList[i];
								columnType = columnTypes[i];

								if (rowValue == null || rowValue.GetType() != columnType)
								{
									items[i] = ConvertObject(rowValue, columnType, context);
								}
								else
								{
									items[i] = rowValue;
								}
							}

							dataTable.Rows.Add(items);
						}
					}
					finally
					{
						dataTable.EndLoadData();
					}
				}
			}
		}

		private static void SetDataTable(IDictionary<string, object> obj, DataTable dataTable,
			JaysonDeserializationContext context)
		{
			dataTable.BeginInit();
			try
			{
				SetDataTableProperties(obj, dataTable, context);
				SetDataTableColumns(obj, dataTable, context);
				SetDataTablePrimaryKey(obj, dataTable, context);
				SetDataTableRows(obj, dataTable, context);
			}
			finally
			{
				dataTable.EndInit();
			}
		}

		private static void SetDataRelations(IDictionary<string, object> obj, DataSet dataSet,
			JaysonDeserializationContext context)
		{
			object relationsObj;
			if (obj.TryGetValue("Rel", out relationsObj) || obj.TryGetValue("rel", out relationsObj))
			{
				var relationsList = (IList)relationsObj;

				object propValue;
				string relationName;
				string tableName;
				string columnName;
				string tableNamespace;
				Dictionary<string, object> relationInfo;

				DataTable childTable;
				DataTable parentTable;
				DataColumnCollection columns;

				List<DataColumn> childColumns = new List<DataColumn>();
				List<DataColumn> parentColumns = new List<DataColumn>();

				foreach (var relationInfoObj in relationsList)
				{
					relationInfo = (Dictionary<string, object>)relationInfoObj;

					relationName = null;
					if ((relationInfo.TryGetValue("Rn", out propValue) ||
						relationInfo.TryGetValue("rn", out propValue)) && propValue != null)
					{
						relationName = (string)propValue;
						if (dataSet.Relations.Contains(relationName))
							continue;

						tableName = null;
						if ((relationInfo.TryGetValue("CTab", out propValue) ||
							relationInfo.TryGetValue("ctab", out propValue)) && propValue != null)
						{
							tableName = (string)propValue;

							tableNamespace = null;
							if ((relationInfo.TryGetValue("CTabNs", out propValue) ||
								relationInfo.TryGetValue("ctabns", out propValue)) && propValue != null)
							{
								tableNamespace = (string)propValue;
							}

							childTable = String.IsNullOrEmpty(tableNamespace) ? (dataSet.Tables.Contains(tableName) ? dataSet.Tables[tableName] : null) :
								(dataSet.Tables.Contains(tableName, tableNamespace) ? dataSet.Tables[tableName, tableNamespace] : null);

							if (childTable != null)
							{
								childColumns.Clear();
								if (relationInfo.TryGetValue("CCol", out propValue) || relationInfo.TryGetValue("ccol", out propValue))
								{
									columns = childTable.Columns;
									foreach (var columnNameObj in (IList)propValue)
									{
										columnName = (string)columnNameObj;
										if (columns.Contains(columnName))
										{
											childColumns.Add(columns[columnName]);
										}
									}
								}

								if (childColumns.Count > 0)
								{
									tableName = null;
									if ((relationInfo.TryGetValue("PTab", out propValue) ||
										relationInfo.TryGetValue("ptab", out propValue)) && propValue != null)
									{
										tableName = (string)propValue;

										tableNamespace = null;
										if ((relationInfo.TryGetValue("PTabNs", out propValue) ||
											relationInfo.TryGetValue("ptabns", out propValue)) && propValue != null)
										{
											tableNamespace = (string)propValue;
										}

										parentTable = String.IsNullOrEmpty(tableNamespace) ? (dataSet.Tables.Contains(tableName) ? dataSet.Tables[tableName] : null) :
											(dataSet.Tables.Contains(tableName, tableNamespace) ? dataSet.Tables[tableName, tableNamespace] : null);

										if (parentTable != null)
										{
											parentColumns.Clear();
											if (relationInfo.TryGetValue("PCol", out propValue) || relationInfo.TryGetValue("pcol", out propValue))
											{
												columns = parentTable.Columns;
												foreach (var columnNameObj in (IList)propValue)
												{
													columnName = (string)columnNameObj;
													if (columns.Contains(columnName))
													{
														parentColumns.Add(columns[columnName]);
													}
												}
											}

											if (parentColumns.Count > 0)
											{
												dataSet.Relations.Add(relationName, parentColumns.ToArray(), childColumns.ToArray());
											}
										}
									}
								}
							}
						}
					}
				}
			}
		}

		private static void SetDataSetProperties(IDictionary<string, object> obj, DataSet dataSet,
			JaysonDeserializationContext context)
		{
			object propValue;
			if (obj.TryGetValue("Cs", out propValue) || obj.TryGetValue("cs", out propValue))
			{
				if (propValue is bool)
				{
					dataSet.CaseSensitive = (bool)propValue;
				}
				else
					if (propValue is string)
					{
						dataSet.CaseSensitive = (string)propValue == "true";
					}
			}

			if (obj.TryGetValue("Dsn", out propValue) || obj.TryGetValue("dsn", out propValue))
			{
				dataSet.DataSetName = (string)propValue;
			}

			if (obj.TryGetValue("Ec", out propValue) || obj.TryGetValue("ec", out propValue))
			{
				if (propValue is bool)
				{
					dataSet.EnforceConstraints = (bool)propValue;
				}
				else
					if (propValue is string)
					{
						dataSet.EnforceConstraints = (string)propValue == "true";
					}
			}

			if (obj.TryGetValue("Lcl", out propValue) || obj.TryGetValue("lcl", out propValue))
			{
				dataSet.Locale = CultureInfo.GetCultureInfo((string)propValue);
			}

			if (obj.TryGetValue("Ns", out propValue) || obj.TryGetValue("ns", out propValue))
			{
				dataSet.Namespace = (string)propValue;
			}

			if (obj.TryGetValue("Pfx", out propValue) || obj.TryGetValue("pfx", out propValue))
			{
				dataSet.Prefix = (string)propValue;
			}

			if (obj.TryGetValue("Ssm", out propValue) || obj.TryGetValue("ssm", out propValue))
			{
				if (propValue is SchemaSerializationMode)
				{
					dataSet.SchemaSerializationMode = (SchemaSerializationMode)propValue;
				}
				else
					if (propValue is string)
					{
						dataSet.SchemaSerializationMode = (SchemaSerializationMode)JaysonEnumCache.Parse((string)propValue, typeof(SchemaSerializationMode));
					}
			}

			if (obj.TryGetValue("Ep", out propValue) || obj.TryGetValue("ep", out propValue))
			{
				SetExtendedProperties(dataSet.ExtendedProperties, propValue, context);
			}
		}

		private static bool GetTableName(IDictionary<string, object> tableObj, out string tableName, out string tableNamespace)
		{
			tableName = null;
			tableNamespace = null;

			object propValue;

			if (tableObj.TryGetValue("Tn", out propValue) || tableObj.TryGetValue("tn", out propValue))
			{
				tableName = (string)propValue;
				if (tableObj.TryGetValue("Ns", out propValue) || tableObj.TryGetValue("ns", out propValue))
				{
					tableNamespace = (string)propValue;
				}
				return true;
			}
			return false;
		}

		private static void SetDataTables(IDictionary<string, object> obj, DataSet dataSet,
			JaysonDeserializationContext context)
		{
			object tablesObj;
			if (obj.TryGetValue("Tab", out tablesObj) || obj.TryGetValue("tab", out tablesObj))
			{
				var tableList = (IList)tablesObj;

				if (tableList.Count > 0)
				{
					DataTable dataTable;
					string tableName;
					string tableNamespace;

					IDictionary<string, object> table;

					foreach (var tableObj in tableList)
					{
						dataTable = null;
						table = tableObj as IDictionary<string, object>;

						if (GetTableName(table, out tableName, out tableNamespace))
						{
							if (String.IsNullOrEmpty(tableNamespace))
							{
								if (dataSet.Tables.Contains(tableName))
								{
									dataTable = dataSet.Tables[tableName];
								}
							}
							else
								if (dataSet.Tables.Contains(tableName, tableNamespace))
								{
									dataTable = dataSet.Tables[tableName, tableNamespace];
								}
						}

						if (dataTable != null)
						{
							SetDataTable(table, dataTable, context);
						}
						else
						{
							dataTable = ConvertObject(tableObj, typeof(DataTable), context) as DataTable;
							if (dataTable != null)
							{
								if (String.IsNullOrEmpty(dataTable.Namespace))
								{
									if (!dataSet.Tables.Contains(dataTable.TableName))
									{
										dataSet.Tables.Add(dataTable);
									}
								}
								else
									if (!dataSet.Tables.Contains(dataTable.TableName, dataTable.Namespace))
									{
										dataSet.Tables.Add(dataTable);
									}
							}
						}
					}
				}
			}
		}

		private static void SetDataSet(IDictionary<string, object> obj, DataSet dataSet,
			JaysonDeserializationContext context)
		{
			dataSet.BeginInit();
			try
			{
				SetDataSetProperties(obj, dataSet, context);
				SetDataTables(obj, dataSet, context);
				SetDataRelations(obj, dataSet, context);
			}
			finally
			{
				dataSet.EndInit();
			}
		}

		# endregion Convert DataTable & DataSet

		private static void SetDictionary(IDictionary<string, object> obj, ref object instance,
			JaysonDeserializationContext context)
		{
			if (instance != null && obj != null && obj.Count > 0 && !(instance is DBNull))
			{
				if (instance is IDictionary<string, object>)
				{
					SetDictionaryStringKey(obj, (IDictionary<string, object>)instance, context);
					return;
				}

				if (instance is IDictionary)
				{
					SetDictionaryIDictionary(obj, (IDictionary)instance, context);
					return;
				}

				if (instance is DataTable)
				{
					SetDataTable(obj, (DataTable)instance, context);
					return;
				}

				if (instance is DataSet)
				{
					SetDataSet(obj, (DataSet)instance, context);
					return;
				}

				if (instance is NameValueCollection)
				{
					SetDictionaryNameValueCollection(obj, (NameValueCollection)instance, context);
					return;
				}

				if (instance is StringDictionary)
				{
					SetDictionaryStringDictionary(obj, (StringDictionary)instance, context);
					return;
				}

				Type instanceType = instance.GetType();

				if (JaysonTypeInfo.IsAnonymous(instanceType))
				{
					SetDictionaryAnonymousType(obj, instance, instanceType, context);
					return;
				}

				if (SetDictionaryGeneric(obj, instance, instanceType, context))
				{
					return;
				}

				SetDictionaryClassOrStruct(obj, ref instance, instanceType, context);
			}
		}

		private static void SetDictionaryClassOrStruct(IDictionary<string, object> obj, ref object instance, 
			Type instanceType, JaysonDeserializationContext context)
		{
			var info = JaysonTypeInfo.GetTypeInfo (instanceType);
			if (!info.JPrimitive)
			{
				var settings = context.Settings;

				bool caseSensitive = settings.CaseSensitive;
				bool raiseErrorOnMissingMember = settings.RaiseErrorOnMissingMember;

				IJaysonFastMember member;
				IDictionary<string, IJaysonFastMember> members = JaysonFastMemberCache.GetMembers(instanceType, caseSensitive);

				string memberName;
				object memberValue;

				bool hasStype = obj.ContainsKey("$type");
				JaysonTypeOverride typeOverride = settings.GetTypeOverride(instanceType);

				foreach (var entry in obj)
				{
					memberName = caseSensitive ? entry.Key : entry.Key.ToLower(JaysonConstants.InvariantCulture);
					if (!hasStype || memberName != "$type")
					{
						if (!members.TryGetValue(memberName, out member) && typeOverride != null)
						{
							memberName = typeOverride.GetAliasMember(memberName);
							if (!String.IsNullOrEmpty(memberName))
							{
								if (!caseSensitive)
								{
									memberName = memberName.ToLower(JaysonConstants.InvariantCulture);
								}
								members.TryGetValue(memberName, out member);
							}
						}

						if (member != null)
						{
							if (typeOverride == null || !typeOverride.IsMemberIgnored(memberName))
							{
								if (member.CanWrite)
								{
									member.Set(ref instance, ConvertObject(entry.Value, member.MemberType, context));
								}
								else if (entry.Value != null)
								{
									memberValue = member.Get(instance);
									if (instance != null)
									{
										SetObject(memberValue, entry.Value, context);
									}
								}
							}
						}
						else if (raiseErrorOnMissingMember)
						{
							throw new JaysonException("Missing member: " + entry.Key);
						}
					}
				}
			}
		}

		private static bool SetDictionaryGeneric(IDictionary<string, object> obj, object instance, Type instanceType,
			JaysonDeserializationContext context)
		{
			Type[] genArgs = JaysonCommon.GetGenericDictionaryArgs(instanceType);
			if (genArgs != null)
			{
				Action<object, object[]> addMethod = JaysonCommon.GetIDictionaryAddMethod(instanceType);
				if (addMethod != null)
				{
					bool changeValue = genArgs[1] != typeof(object);
					bool changeKey = !(genArgs[0] == typeof(object) || genArgs[0] == typeof(string));

					object key;
					object value;

					Type keyType = genArgs[0];
					Type valType = genArgs[1];

					object kvListObj;
					if (obj.TryGetValue("$kv", out kvListObj))
					{
						List<object> kvList = kvListObj as List<object>;
						if (kvList != null)
						{
							int count = kvList.Count;
							if (count > 0)
							{
								IDictionary<string, object> kvp;

								for (int i = 0; i < count; i++)
								{
									kvp = (IDictionary<string, object>)kvList[i];

									key = !changeKey ? kvp["$k"] : ConvertObject(kvp["$k"], keyType, context);
									value = !changeValue ? kvp["$v"] : ConvertObject(kvp["$v"], valType, context);

									addMethod(instance, new object[] { key, value });
								}
							}
						}
						return true;
					}

					bool hasStype = obj.ContainsKey("$type");

					foreach (var entry in obj)
					{
						if (!hasStype || entry.Key != "$type")
						{
							key = changeKey ? ConvertObject(entry.Key, keyType, context) : entry.Key;
							value = changeValue ? ConvertObject(entry.Value, valType, context) : entry.Value;

							addMethod(instance, new object[] { key, value });
						}
					}

					return true;
				}
			}
			return false;
		}

		private static void SetDictionaryAnonymousType(IDictionary<string, object> obj, object instance, Type instanceType,
			JaysonDeserializationContext context)
		{
			var settings = context.Settings;
			if (!settings.IgnoreAnonymousTypes)
			{
				bool caseSensitive = settings.CaseSensitive;
				bool raiseErrorOnMissingMember = settings.RaiseErrorOnMissingMember;

				IDictionary<string, IJaysonFastMember> members =
					JaysonFastMemberCache.GetAllFieldMembers(instanceType, caseSensitive);

				if (members == null || members.Count == 0)
				{
					if (raiseErrorOnMissingMember)
					{
						throw new JaysonException("Missing members");
					}
					return;
				}

				IJaysonFastMember member;

				bool hasStype = obj.ContainsKey("$type");
				JaysonTypeOverride typeOverride = settings.GetTypeOverride(instanceType);

				string key;
				string memberName;
				object memberValue;

				foreach (var entry in obj)
				{
					if (!hasStype || entry.Key != "$type")
					{
						key = entry.Key;

						memberName = "<" + (caseSensitive ? key : key.ToLower(JaysonConstants.InvariantCulture)) + ">";
						if (!members.TryGetValue(memberName, out member) && typeOverride != null)
						{
							key = typeOverride.GetAliasMember(key);
							if (!String.IsNullOrEmpty(key))
							{
								memberName = "<" + (caseSensitive ? key : key.ToLower(JaysonConstants.InvariantCulture)) + ">";
								members.TryGetValue(memberName, out member);
							}
						}

						if (member != null)
						{
							if (typeOverride == null || !typeOverride.IsMemberIgnored(memberName))
							{
								if (member.CanWrite)
								{
									member.Set(ref instance, ConvertObject(entry.Value, member.MemberType, context));
								}
								else if (entry.Value != null)
								{
									memberValue = member.Get(instance);
									if (instance != null)
									{
										SetObject(memberValue, entry.Value, context);
									}
								}
							}
						}
						else if (raiseErrorOnMissingMember)
						{
							throw new JaysonException("Missing member: " + entry.Key);
						}
					}
				}
			}
		}

		private static void SetDictionaryStringDictionary(IDictionary<string, object> obj, StringDictionary instance,
			JaysonDeserializationContext context)
		{
			string key;
			object value;
			Type valueType;

			object kvListObj;
			if (obj.TryGetValue("$kv", out kvListObj))
			{
				List<object> kvList = kvListObj as List<object>;

				if (kvList != null)
				{
					int count = kvList.Count;
					if (count > 0)
					{
						object keyObj;
						IDictionary<string, object> kvp;

						for (int i = 0; i < count; i++)
						{
							kvp = (IDictionary<string, object>)kvList[i];

							keyObj = ConvertObject(kvp["$k"], typeof(object), context);
							key = keyObj as string ?? keyObj.ToString();

							value = ConvertObject(kvp["$v"], typeof(object), context);

							if (value == null || value is string)
							{
								instance.Add(key, (string)value);
							}
							else
							{
								valueType = value.GetType();
								if (JaysonTypeInfo.IsJPrimitive(valueType))
								{
									instance.Add(key, JaysonFormatter.ToString(value, valueType));
								}
								else if (value is IFormattable)
								{
									instance.Add(key, ((IFormattable)value).ToString(null, JaysonConstants.InvariantCulture));
								}
								else
								{
									instance.Add(key, ToJsonString(value));
								}
							}
						}
					}
				}
				return;
			}

			bool hasStype = obj.ContainsKey("$type");

			foreach (var item in obj)
			{
				key = item.Key;
				if (!hasStype || key != "$type")
				{
					value = item.Value;
					if (value == null || value is string)
					{
						instance.Add(key, (string)value);
					}
					else
					{
						valueType = value.GetType();
						if (JaysonTypeInfo.IsJPrimitive(valueType))
						{
							instance.Add(key, JaysonFormatter.ToString(value, valueType));
						}
						else if (value is IFormattable)
						{
							instance.Add(key, ((IFormattable)value).ToString(null, JaysonConstants.InvariantCulture));
						}
						else
						{
							instance.Add(key, ToJsonString(value));
						}
					}
				}
			}
		}

		private static void SetDictionaryNameValueCollection(IDictionary<string, object> obj, NameValueCollection instance,
			JaysonDeserializationContext context)
		{
			string key;
			object value;
			Type valueType;

			object kvListObj;
			if (obj.TryGetValue("$kv", out kvListObj))
			{
				List<object> kvList = kvListObj as List<object>;

				if (kvList != null)
				{
					int count = kvList.Count;
					if (count > 0)
					{
						object keyObj;
						IDictionary<string, object> kvp;

						for (int i = 0; i < count; i++)
						{
							kvp = (IDictionary<string, object>)kvList[i];

							keyObj = ConvertObject(kvp["$k"], typeof(object), context);
							key = keyObj as string ?? keyObj.ToString();

							value = ConvertObject(kvp["$v"], typeof(object), context);

							if (value == null || value is string)
							{
								instance.Add(key, (string)value);
							}
							else
							{
								valueType = value.GetType();
								if (JaysonTypeInfo.IsJPrimitive(valueType))
								{
									instance.Add(key, JaysonFormatter.ToString(value, valueType));
								}
								else if (value is IFormattable)
								{
									instance.Add(key, ((IFormattable)value).ToString(null, JaysonConstants.InvariantCulture));
								}
								else
								{
									instance.Add(key, ToJsonString(value));
								}
							}

						}
					}
				}
				return;
			}

			bool hasStype = obj.ContainsKey("$type");

			foreach (var item in obj)
			{
				key = item.Key;
				if (!hasStype || key != "$type")
				{
					value = item.Value;
					if (value == null || value is string)
					{
						instance.Add(key, (string)value);
					}
					else
					{
						valueType = value.GetType();
						if (JaysonTypeInfo.IsJPrimitive(valueType))
						{
							instance.Add(key, JaysonFormatter.ToString(value, valueType));
						}
						else if (value is IFormattable)
						{
							instance.Add(key, ((IFormattable)value).ToString(null, JaysonConstants.InvariantCulture));
						}
						else
						{
							instance.Add(key, ToJsonString(value));
						}
					}
				}
			}
		}

		private static void SetDictionaryIDictionary(IDictionary<string, object> obj, IDictionary instance, JaysonDeserializationContext context)
		{
			object key;
			object kvListObj;

			Type[] genArgs = JaysonCommon.GetGenericDictionaryArgs(instance.GetType());

			if (genArgs != null)
			{
				bool hasStype = obj.ContainsKey("$type");

				bool changeValue = hasStype || (genArgs[1] != typeof(object));
				bool changeKey = !(genArgs[0] == typeof(object) || genArgs[0] == typeof(string));

				Type keyType = genArgs[0];
				Type valType = genArgs[1];

				if (obj.TryGetValue("$kv", out kvListObj))
				{
					List<object> kvList = kvListObj as List<object>;
					if (kvList != null)
					{
						int count = kvList.Count;
						if (count > 0)
						{
							object keyObj;
							IDictionary<string, object> kvp;

							for (int i = 0; i < count; i++)
							{
								kvp = (IDictionary<string, object>)kvList[i];

								keyObj = !changeKey ? kvp["$k"] : ConvertObject(kvp["$k"], keyType, context);
								instance[keyObj] = !changeValue ? kvp["$v"] : ConvertObject(kvp["$v"], valType, context);
							}
						}
					}
					return;
				}

				foreach (var entry in obj)
				{
					if (!hasStype || entry.Key != "$type")
					{
						key = changeKey ? ConvertObject(entry.Key, keyType, context) : entry.Key;
						instance[key] = !changeValue ? entry.Value : ConvertObject(entry.Value, valType, context);
					}
				}
				return;
			}

			if (obj.TryGetValue("$kv", out kvListObj))
			{
				List<object> kvList = kvListObj as List<object>;
				if (kvList != null)
				{
					int count = kvList.Count;
					if (count > 0)
					{
						object keyObj;
						IDictionary<string, object> kvp;

						for (int i = 0; i < count; i++)
						{
							kvp = (IDictionary<string, object>)kvList[i];

							keyObj = ConvertObject(kvp["$k"], typeof(object), context);
							instance[keyObj] = ConvertObject(kvp["$v"], typeof(object), context);
						}
					}
				}
				return;
			}

			bool hasStype2 = obj.ContainsKey("$type");

			foreach (var entry in obj)
			{
				if (!hasStype2 || entry.Key != "$type")
				{
					instance[entry.Key] = ConvertObject(entry.Value, typeof(object), context);
				}
			}
		}

		private static void SetDictionaryStringKey(IDictionary<string, object> obj, IDictionary<string, object> instance,
			JaysonDeserializationContext context)
		{
			string key;
			object kvListObj;
			if (obj.TryGetValue("$kv", out kvListObj))
			{
				List<object> kvList = kvListObj as List<object>;
				if (kvList != null)
				{
					int count = kvList.Count;
					if (count > 0)
					{
						object keyObj;
						IDictionary<string, object> kvp;

						for (int i = 0; i < count; i++)
						{
							kvp = (IDictionary<string, object>)kvList[i];

							keyObj = ConvertObject(kvp["$k"], typeof(object), context);
							key = keyObj as string ?? keyObj.ToString();

							instance[key] = ConvertObject(kvp["$v"], typeof(object), context);
						}
					}
				}
				return;
			}

			bool hasStype = obj.ContainsKey("$type");

			foreach (var entry in obj)
			{
				key = entry.Key;
				if (!hasStype || key != "$type")
				{
					instance[key] = ConvertObject(entry.Value, typeof(object), context);
				}
			}
		}

		private static void SetMultiDimensionalArray(IList obj, Array instance, Type arrayType,
			int rank, int currRank, int[] rankIndices, JaysonDeserializationContext context)
		{
			int length = Math.Min(obj.Count, instance.GetLength(currRank));
			if (length > 0)
			{
				if (currRank == rank - 1)
				{
					for (int i = 0; i < length; i++)
					{
						rankIndices[currRank] = i;
						instance.SetValue(ConvertObject(obj[i], arrayType, context), rankIndices);
					}
				}
				else
				{
					object child;
					for (int i = 0; i < length; i++)
					{
						rankIndices[currRank] = i;
						child = obj[i];

						if (child is IList)
						{
							SetMultiDimensionalArray((IList)child, instance, arrayType, rank, currRank + 1,
								rankIndices, context);
						}
						else if (child is IList<object>)
						{
							SetMultiDimensionalArray((IList<object>)child, instance, arrayType, rank, currRank + 1,
								rankIndices, context);
						}
					}
				}
			}
		}

		private static void SetMultiDimensionalArray(IList<object> obj, Array instance, Type arrayType,
			int rank, int currRank, int[] rankIndices, JaysonDeserializationContext context)
		{
			int length = Math.Min(obj.Count, instance.GetLength(currRank));
			if (length > 0)
			{
				if (currRank == rank - 1)
				{
					for (int i = 0; i < length; i++)
					{
						rankIndices[currRank] = i;
						instance.SetValue(ConvertObject(obj[i], arrayType, context), rankIndices);
					}
				}
				else
				{
					object child;
					for (int i = 0; i < length; i++)
					{
						rankIndices[currRank] = i;
						child = obj[i];

						if (child is IList)
						{
							SetMultiDimensionalArray((IList)child, instance, arrayType, rank, currRank + 1,
								rankIndices, context);
						}
						else if (child is IList<object>)
						{
							SetMultiDimensionalArray((IList<object>)child, instance, arrayType, rank, currRank + 1,
								rankIndices, context);
						}
					}
				}
			}
		}

		private static void SetList(IList<object> obj, object instance, JaysonDeserializationContext context)
		{
			if (instance == null || obj == null || obj.Count == 0 || instance is DBNull)
			{
				return;
			}

			var info = JaysonTypeInfo.GetTypeInfo(instance.GetType());
			if (info.Array)
			{
				Array aResult = (Array)instance;
				Type arrayType = info.ElementType;

				int rank = info.ArrayRank;
				if (rank == 1)
				{
					int count = obj.Count;
					for (int i = 0; i < count; i++)
					{
						aResult.SetValue(ConvertObject(obj[i], arrayType, context), i);
					}
				}
				else
					if (aResult.GetLength(rank - 1) > 0)
					{
						SetMultiDimensionalArray(obj, aResult, arrayType, rank, 0, new int[rank], context);
					}

				return;
			}

			if (instance is IList) {
				int count = obj.Count;
				IList lResult = (IList)instance;

				object item;
				Type argType = JaysonCommon.GetGenericListArgs (info.Type);
				if (argType != null) {
					#if (NET3500 || NET3000 || NET2000)
					bool isNullable = JaysonTypeInfo.IsNullable(argType);

					Action<object, object[]> add = null;
					if (isNullable)
					{
						add = JaysonCommon.GetICollectionAddMethod(info.Type);
					}
					#endif

					for (int i = 0; i < count; i++) {
						#if !(NET3500 || NET3000 || NET2000)
						lResult.Add (ConvertObject (obj [i], argType, context));
						#else
						if (!isNullable)
						{
							lResult.Add(ConvertObject(obj[i], argType, context));
						}
						else
						{
							item = ConvertObject(obj[i], argType, context);
							if (item == null)
							{
								add(lResult, new object[] { null });
							}
							else
							{
								lResult.Add(item);
							}
						}
						#endif
					}
					return;
				}

				for (int i = 0; i < count; i++) {
					item = obj [i];
					if (item is IDictionary<string, object>) {
						item = ConvertDictionary ((IDictionary<string, object>)item, typeof(object), context);
					} else if (item is IList<object>) {
						item = ConvertList ((IList<object>)item, typeof(object), context);
					}

					lResult.Add (item);
				}
			} 
			else
			{
				Type argType = JaysonCommon.GetGenericCollectionArgs(info.Type);
				if (argType != null) {
					Action<object, object[]> methodInfo = JaysonCommon.GetICollectionAddMethod (info.Type);
					if (methodInfo != null) {
						int count = obj.Count;
						for (int i = 0; i < count; i++) {
							methodInfo (instance, new object[] { ConvertObject (obj [i], argType, context) });
						}
						return;
					}
				} 

				if (instance is Stack) {
					Stack stack = (Stack)instance;

					int count = obj.Count;
					for (int i = count-1; i > -1; i--)
					{
						stack.Push(ConvertObject(obj[i], typeof(object), context));
					}
					return;
				} 

				if (instance is Queue) {
					Queue queue = (Queue)instance;

					int count = obj.Count;
					for (int i = 0; i < count; i++)
					{
						queue.Enqueue(ConvertObject(obj[i], typeof(object), context));
					}
					return;
				}

				Type[] argTypes = info.GenericArguments;

				if (argTypes != null && argTypes.Length == 1) {
					Action<object, object[]> methodInfo;

					methodInfo = JaysonCommon.GetStackPushMethod (info.Type);
					if (methodInfo != null) {
						argType = argTypes[0];
						int count = obj.Count;

						for (int i = count-1; i > -1; i--) {
							methodInfo (instance, new object[] { ConvertObject (obj [i], argType, context) });
						}
						return;
					}

					methodInfo = JaysonCommon.GetQueueEnqueueMethod (info.Type);
					if (methodInfo != null) {
						argType = argTypes[0];
						int count = obj.Count;

						for (int i = 0; i < count; i++) {
							methodInfo (instance, new object[] { ConvertObject (obj [i], argType, context) });
						}
						return;
					}

					#if !(NET3500 || NET3000 || NET2000)
					methodInfo = JaysonCommon.GetConcurrentBagMethod(info.Type);
					if (methodInfo != null)
					{
						argType = argTypes[0];
						int count = obj.Count;

						if (JaysonCommon.IsOnMono ()) {
							for (int i = 0; i < count; i++)
							{
								methodInfo(instance, new object[] { ConvertObject(obj[i], argType, context) });
							}
						} else {
							for (int i = count - 1; i > -1; i--)
							{
								methodInfo(instance, new object[] { ConvertObject(obj[i], argType, context) });
							}
						}
						return;
					}

					if (JaysonCommon.IsProducerConsumerCollection (info.Type)) {
						methodInfo = JaysonCommon.GetIProducerConsumerCollectionAddMethod (info.Type);

						if (methodInfo != null) {
							argType = argTypes[0];
							int count = obj.Count;

							for (int i = 0; i < count; i++) {
								methodInfo (instance, new object[] { ConvertObject (obj [i], argType, context) });
							}
							return;
						}
					}
					#endif
				}
			}
		}

		private static void SetObject(object obj, object instance, JaysonDeserializationContext context)
		{
			if (instance != null && obj != null)
			{
				Type instanceType = instance.GetType();
				if (!JaysonTypeInfo.IsJPrimitive(instanceType))
				{
					if (obj is IDictionary<string, object>)
					{
						SetDictionary((IDictionary<string, object>)obj, ref instance, context);
					}
					else if (obj is IList<object>)
					{
						SetList((IList<object>)obj, instance, context);
					}
				}
			}
		}

		private static Type EvaluateListType(Type type, out bool asList, out bool asArray, out bool asReadOnly)
		{
			asList = false;
			asArray = false;
			asReadOnly = false;

			var info = JaysonTypeInfo.GetTypeInfo(type);
			if (info.Array)
			{
				asArray = true;
				return type;
			}

			if (type == typeof(List<object>))
			{
				asList = true;
				return type;
			}

			if (type == typeof(IList) || type == typeof(ArrayList))
			{
				asList = true;
				return typeof(ArrayList);
			}

			if (!info.Generic)
			{
				return type;
			}

			var genericType = info.GenericTypeDefinitionType;

			if (genericType == typeof(ReadOnlyCollection<>))
			{
				asList = true;
				asReadOnly = true;
				return GetGenericUnderlyingList(info);
			}

			if (info.Interface)
			{
				#if !(NET4000 || NET3500 || NET3000 || NET2000)
				if (genericType == typeof(IList<>) ||
					genericType == typeof(ICollection<>) ||
					genericType == typeof(IEnumerable<>) ||
					genericType == typeof(IReadOnlyCollection<>))
				{
					asList = true;
					return GetGenericUnderlyingList(info);
				}
				#else
				if (genericType == typeof(IList<>) ||
				genericType == typeof(ICollection<>) || 
				genericType == typeof(IEnumerable<>)) {
				asList = true;
				return GetGenericUnderlyingList(info);
				}
				#endif
			}
			return type;
		}

		private static Type GetEvaluatedListType(Type type, out bool asList, out bool asArray, out bool asReadOnly)
		{
			EvaluatedListType elt;
			if (!s_EvaluatedListTypeCache.TryGetValue(type, out elt))
			{
				var eType = EvaluateListType(type, out asList, out asArray, out asReadOnly);
				s_EvaluatedListTypeCache[type] = new EvaluatedListType
				{
					EvaluatedType = eType,
					AsList = asList,
					AsArray = asArray,
					AsReadOnly = asReadOnly,
				};

				return eType;
			}

			asList = elt.AsList;
			asArray = elt.AsArray;
			asReadOnly = elt.AsReadOnly;

			return elt.EvaluatedType;
		}

		private static Type EvaluateDictionaryType(Type type, out bool asDictionary, out bool asReadOnly)
		{
			asReadOnly = false;
			asDictionary = false;
			if (type == typeof(Dictionary<string, object>))
			{
				asDictionary = true;
				return type;
			}

			if (type == typeof(IDictionary) || type == typeof(Hashtable))
			{
				asDictionary = true;
				return typeof(Hashtable);
			}

			var info = JaysonTypeInfo.GetTypeInfo(type);
			if (!info.Generic)
			{
				return type;
			}

			var genericType = info.GenericTypeDefinitionType;

			#if !(NET4000 || NET3500 || NET3000 || NET2000)
			if (genericType == typeof(ReadOnlyDictionary<,>))
			{
				asReadOnly = true;
				asDictionary = true;
				return GetGenericUnderlyingDictionary(info);
			}
			#endif

			if (info.Interface)
			{
				#if !(NET4000 || NET3500 || NET3000 || NET2000)
				if (genericType == typeof(IDictionary<,>) ||
					genericType == typeof(IReadOnlyDictionary<,>))
				{
					asDictionary = true;
					return GetGenericUnderlyingDictionary(info);
				}
				#else
				if (genericType == typeof(IDictionary<,>)) {
				asDictionary = true;
				return GetGenericUnderlyingDictionary(info);
				} 
				#endif

				if (genericType == typeof(ICollection<>) || genericType == typeof(IEnumerable<>))
				{
					Type firstArgType = info.GenericArguments[0];

					if (firstArgType == typeof(KeyValuePair<string, object>) ||
						firstArgType == typeof(KeyValuePair<object, object>))
					{
						asDictionary = true;
						return GetGenericUnderlyingDictionary(info);
					}

					var argInfo = JaysonTypeInfo.GetTypeInfo(firstArgType);
					if (argInfo.Generic && argInfo.GenericTypeDefinitionType == typeof(KeyValuePair<,>))
					{
						asDictionary = true;
						return GetGenericUnderlyingDictionary(info);
					}
				}
			}
			return type;
		}

		private static Type GetEvaluatedDictionaryType(Type type, out bool asDictionary, out bool asReadOnly)
		{
			EvaluatedDictionaryType edt;
			if (!s_EvaluatedDictionaryTypeCache.TryGetValue(type, out edt))
			{
				var eType = EvaluateDictionaryType(type, out asDictionary, out asReadOnly);
				s_EvaluatedDictionaryTypeCache[type] = new EvaluatedDictionaryType
				{
					EvaluatedType = eType,
					AsDictionary = asDictionary,
					AsReadOnly = asReadOnly
				};

				return eType;
			}

			asDictionary = edt.AsDictionary;
			asReadOnly = edt.AsReadOnly;

			return edt.EvaluatedType;
		}

		private static object CtorParamMatcher(string ctorParamName, IDictionary<string, object> obj)
		{
			return obj.FirstOrDefault (kvp => 
				ctorParamName.Equals (kvp.Key, StringComparison.OrdinalIgnoreCase)).Value;
		}

		private static object ConvertDictionary(IDictionary<string, object> obj, Type toType,
			JaysonDeserializationContext context, bool forceType = false)
		{
			object Stype;
			bool binded = false;

			var settings = context.Settings;

			JaysonTypeInfo info = null;
			if (obj.TryGetValue("$type", out Stype) && Stype != null)
			{
				info = JaysonTypeInfo.GetTypeInfo(toType);
				Type instanceType = null;

				if (!forceType || toType == typeof(object) || info.Interface)
				{
					string typeName = Stype as string;

					if (!String.IsNullOrEmpty(typeName))
					{
						binded = true;
						instanceType = JaysonCommon.GetType(typeName, settings.Binder);
					}
					else if (context.GlobalTypes != null)
					{
						if (Stype is int)
						{
							instanceType = context.GlobalTypes.GetType((int)Stype);
						}
						else if (Stype is long)
						{
							instanceType = context.GlobalTypes.GetType((int)((long)Stype));
						}
					}
				}

				if (instanceType == null && !forceType)
				{
					instanceType = toType;
				}

				if (instanceType != null)
				{
					JaysonTypeOverride typeOverride = settings.GetTypeOverride(instanceType);
					if (typeOverride != null)
					{
						Type bindToType = typeOverride.BindToType;
						if (bindToType != null)
						{
							instanceType = bindToType;
						}
					}

					if (instanceType != null && instanceType != toType)
					{
						toType = instanceType;
						info = JaysonTypeInfo.GetTypeInfo(toType);
					}
				}

				if (settings.IgnoreAnonymousTypes && (info != null) && info.Anonymous)
				{
					return null;
				}

				object Svalues;
				if (obj.TryGetValue("$value", out Svalues))
				{
					return ConvertObject(Svalues, toType, context);
				}

				if (obj.TryGetValue("$values", out Svalues) && (Svalues is IList<object>))
				{
					return ConvertList((IList<object>)Svalues, toType, context);
				}
			}

			if (obj.TryGetValue("$dt", out Stype) && Stype != null)
			{
				string dataType = Stype as string;
				if (dataType != null)
				{
					if (dataType.Equals("Tbl", StringComparison.OrdinalIgnoreCase))
					{
						if (!typeof(DataTable).IsAssignableFrom(toType))
						{
							toType = typeof(DataTable);
						}
					}
					else if (dataType.Equals("Ds", StringComparison.OrdinalIgnoreCase))
					{
						if (!typeof(DataSet).IsAssignableFrom(toType))
						{
							toType = typeof(DataSet);
						}
					}
				}
			}

			bool asReadOnly, asDictionary;
			toType = GetEvaluatedDictionaryType(toType, out asDictionary, out asReadOnly);

			if (!binded)
			{
				Type instanceType = BindToType(settings, toType);
				if (instanceType != null && instanceType != toType)
				{
					toType = instanceType;

					info = JaysonTypeInfo.GetTypeInfo(toType);
					if (settings.IgnoreAnonymousTypes && info.Anonymous)
					{
						return null;
					}
				}
			}

			object result = null;
			if (toType != typeof(object))
			{
				bool useDefaultCtor = true;
				if (settings.ObjectActivator != null)
				{
					result = settings.ObjectActivator(toType, obj, out useDefaultCtor);
				}

				if (useDefaultCtor)
				{
					JaysonCtorInfo ctorInfo = JaysonCtorInfo.GetDefaultCtorInfo (toType);
					if (!ctorInfo.HasParam) {
						result = JaysonObjectConstructor.New (toType);
					} else {
						object paramValue;
						ParameterInfo ctorParam;
						List<object> ctorParams = new List<object> ();

						var matcher = context.Settings.CtorParamMatcher ?? DefaultMatcher;

						for (int i = 0; i < ctorInfo.ParamLength; i++) {
							ctorParam = ctorInfo.CtorParams [i];

							paramValue = matcher (ctorParam.Name, obj);

							if (paramValue != null) {
								if (paramValue.GetType () != ctorParam.ParameterType) {
									paramValue = ConvertObject (paramValue, ctorParam.ParameterType, context);
								}
							} 
							#if !(NET4000 || NET3500 || NET3000 || NET2000)
							else if (ctorParam.HasDefaultValue) {
								paramValue = ctorParam.DefaultValue;
							} 
							#else
							else if ((ctorParam.Attributes & ParameterAttributes.HasDefault) == ParameterAttributes.HasDefault)
							{
							paramValue = ctorParam.DefaultValue;
							}
							#endif
							else {
								paramValue = ConvertObject (null, ctorParam.ParameterType, context);
							}

							ctorParams.Add (paramValue);
						}

						result = ctorInfo.New (ctorParams.ToArray ());
					}
				}
			}
			else
			{
				#if !(NET3500 || NET3000 || NET2000)
				if (settings.DictionaryType == DictionaryDeserializationType.Expando)
				{
					result = new ExpandoObject();
				}
				else
				{
					result = new Dictionary<string, object>(10);
				}
				#else
				result = new Dictionary<string, object>(10);
				#endif
			}

			SetDictionary(obj, ref result, context);

			#if !(NET4000 || NET3500 || NET3000 || NET2000)
			if (asReadOnly)
			{
				return GetReadOnlyDictionaryActivator(toType)(new object[] { result });
			}
			#endif

			return result;
		}

		private static Type BindToType(JaysonDeserializationSettings settings, Type type)
		{
			if (settings.Binder != null)
			{
				string typeName;
				string assemblyName;
				Type instanceType = null;

				#if !(NET3500 || NET3000 || NET2000)
				settings.Binder.BindToName(type, out assemblyName, out typeName);
				#else
				typeName = null;
				assemblyName = null;
				#endif
				if (String.IsNullOrEmpty(typeName))
				{
					instanceType = settings.Binder.BindToType(type.Assembly.FullName, type.FullName);
				}
				else
				{
					instanceType = settings.Binder.BindToType(assemblyName, typeName);
				}

				if (instanceType != null)
				{
					return instanceType;
				}
			}

			JaysonTypeOverride typeOverride = settings.GetTypeOverride(type);
			if (typeOverride != null)
			{
				Type bindToType = typeOverride.BindToType;
				if (bindToType != null)
				{
					return bindToType;
				}
			}

			return type;
		}

		private static int[] GetArrayRankIndices(object obj, int rank)
		{
			int[] result = new int[rank];
			if (obj != null)
			{
				int count;
				int index = 0;

				IList list;
				IList<object> oList;
				ICollection collection;

				do
				{
					list = obj as IList;
					if (list != null)
					{
						count = list.Count;
						if (count > 0)
						{
							obj = list[0];
						}
					}
					else
					{
						oList = obj as IList<object>;
						if (oList != null)
						{
							count = oList.Count;
							if (count > 0)
							{
								obj = oList[0];
							}
						}
						else
						{
							collection = obj as ICollection;
							if (collection != null)
							{
								count = collection.Count;
								obj = null;
							}
							else
							{
								break;
							}
						}
					}

					result[index++] = count;
				} while (index < rank && count > 0 && obj != null);
			}
			return result;
		}

		private static object ConvertList(IList<object> obj, Type toType, JaysonDeserializationContext context)
		{
			if (toType == typeof(byte[]) && obj.Count == 1)
			{
				object item = obj[0];
				if (item is string)
				{
					return Convert.FromBase64String((string)item);
				}
			}

			var settings = context.Settings;

			toType = BindToType(settings, toType);

			if (toType == typeof(object))
			{
				if (toType == typeof(object))
				{
					switch (settings.ArrayType)
					{
					case ArrayDeserializationType.ArrayList:
						toType = typeof(ArrayList);
						break;
					case ArrayDeserializationType.Array:
					case ArrayDeserializationType.ArrayDefined:
						toType = typeof(object[]);
						break;
					default:
						toType = typeof(List<object>);
						break;
					}
				}
				toType = typeof(List<object>);
			}

			bool asList, asArray, asReadOnly;
			Type listType = GetEvaluatedListType(toType, out asList, out asArray, out asReadOnly);

			object result;
			var info = JaysonTypeInfo.GetTypeInfo(listType);
			if (info.Array)
			{
				var rank = info.ArrayRank;
				if (rank == 1)
				{
					result = Array.CreateInstance(info.ElementType, obj.Count);
				}
				else
				{
					result = Array.CreateInstance(info.ElementType, GetArrayRankIndices(obj, rank));
				}
			}
			else if (settings.ObjectActivator == null)
			{
				result = JaysonObjectConstructor.New(listType);
			}
			else
			{
				bool useDefaultCtor;
				result = settings.ObjectActivator(listType, null, out useDefaultCtor);
				if (useDefaultCtor)
				{
					result = JaysonObjectConstructor.New(listType);
				}
			}

			SetList(obj, result, context);
			if (result == null)
			{
				return result;
			}

			Type resultType = result.GetType();
			if (toType == resultType ||
				toType.IsAssignableFrom(resultType))
			{
				return result;
			}

			if (asReadOnly)
			{
				return GetReadOnlyCollectionActivator(toType)(new object[] { result });
			}

			return result;
		}

		private static object ConvertObject(object obj, Type toType, JaysonDeserializationContext context)
		{
			toType = BindToType(context.Settings, toType);
			if (toType == typeof(object))
			{
				if (obj != null)
				{
					if (obj is IDictionary<string, object>)
					{
						obj = ConvertDictionary((IDictionary<string, object>)obj, toType, context);
					}
					else if (obj is IList<object>)
					{
						obj = ConvertList((IList<object>)obj, toType, context);
					}
				}

				return obj;
			}

			if (obj == null)
			{
				var info = JaysonTypeInfo.GetTypeInfo(toType);
				if (info.Class || info.Nullable)
				{
					return null;
				}

				if (context.Settings.ObjectActivator == null)
				{
					return JaysonObjectConstructor.New(toType);
				}

				bool useDefaultCtor;
				object result = context.Settings.ObjectActivator(toType, null, out useDefaultCtor);
				if (useDefaultCtor)
				{
					return JaysonObjectConstructor.New(toType);
				}
				return result;
			}

			Type objType = obj.GetType();
			if (toType == objType || toType.IsAssignableFrom(objType))
			{
				return obj;
			}

			if (toType == typeof(string))
			{
				if (obj is IFormattable)
				{
					return ((IFormattable)obj).ToString(null, JaysonConstants.InvariantCulture);
				}
				if (obj is IConvertible)
				{
					return ((IConvertible)obj).ToString(JaysonConstants.InvariantCulture);
				}
				return obj.ToString();
			}

			if (obj is IDictionary<string, object>)
			{
				return ConvertDictionary((IDictionary<string, object>)obj, toType, context);
			}

			if (obj is IList<object>)
			{
				return ConvertList((IList<object>)obj, toType, context);
			}

			var settings = context.Settings;

			var tInfo = JaysonTypeInfo.GetTypeInfo(toType);
			if (!tInfo.Class)
			{
				bool converted;

				JaysonTypeCode jtc = tInfo.JTypeCode;
				if (jtc == JaysonTypeCode.DateTime)
				{
					return JaysonCommon.TryConvertDateTime(obj, settings.DateTimeFormat, settings.DateTimeZoneType);
				}

				if (jtc == JaysonTypeCode.DateTimeNullable)
				{
					DateTime dt = JaysonCommon.TryConvertDateTime(obj, settings.DateTimeFormat, settings.DateTimeZoneType);

					if (dt == default(DateTime))
					{
						return null;
					}
					return (DateTime?)dt;
				}

				object result = JaysonCommon.ConvertToPrimitive(obj, toType, out converted);
				if (converted)
				{
					return result;
				}

				if (JaysonTypeInfo.IsNullable(toType))
				{
					Type argType = JaysonTypeInfo.GetGenericArguments(toType)[0];
					object value = ConvertObject(obj, argType, context);

					var constructor = toType.GetConstructor(new Type[] { argType });
					if (constructor != null)
					{
						value = constructor.Invoke(new object[] { value });
					}

					return value;
				}
			}

			if (toType == typeof(byte[]) && objType == typeof(string))
			{
				return Convert.FromBase64String((string)obj);
			}

			if (settings.IgnoreAnonymousTypes && JaysonTypeInfo.IsAnonymous(toType))
			{
				return null;
			}

			#if !(NET3500 || NET3000 || NET2000)
			if (settings.Binder != null)
			{
				string typeName;
				string assemblyName;

				settings.Binder.BindToName(toType, out assemblyName, out typeName);
				if (!String.IsNullOrEmpty(typeName))
				{
					Type instanceType = settings.Binder.BindToType(assemblyName, typeName);
					if (instanceType != null)
					{
						toType = instanceType;
					}
				}
			}
			#endif

			if (settings.ObjectActivator == null)
			{
				return JaysonObjectConstructor.New(toType);
			}
			else
			{
				bool useDefaultCtor;
				object result = settings.ObjectActivator(toType, null, out useDefaultCtor);
				if (useDefaultCtor)
				{
					return JaysonObjectConstructor.New(toType);
				}
				return result;
			}
		}

		# region ConvertJsonObject

		public static object ConvertJsonObject(object obj, Type toType, JaysonDeserializationSettings settings = null)
		{
			if (obj == null || obj == DBNull.Value)
			{
				return JaysonTypeInfo.GetDefault(toType);
			}

			var context = new JaysonDeserializationContext
			{
				Text = String.Empty,
				Length = 0,
				Position = 0,
				Settings = settings ?? JaysonDeserializationSettings.Default
			};

			return ConvertObject (obj, toType, context);
		}

		public static ToType ConvertJsonObject<ToType>(object obj, JaysonDeserializationSettings settings = null)
		{
			if (obj == null || obj == DBNull.Value)
			{
				return (ToType)JaysonTypeInfo.GetDefault(typeof(ToType));
			}

			var context = new JaysonDeserializationContext
			{
				Text = String.Empty,
				Length = 0,
				Position = 0,
				Settings = settings ?? JaysonDeserializationSettings.Default
			};

			return (ToType)ConvertObject (obj, typeof(ToType), context);
		}

		# endregion ConvertJsonObject

		# endregion Convert

		# region ToX

		public static IDictionary<string, object> ToDictionary(string str, JaysonDeserializationSettings settings = null)
		{
			if (String.IsNullOrEmpty(str))
			{
				throw new JaysonException("Empty string.");
			}

			if (settings == null)
			{
				settings = new JaysonDeserializationSettings();
				JaysonDeserializationSettings.Default.AssignTo(settings);
				settings.DictionaryType = DictionaryDeserializationType.Dictionary;
			}

			var result = ParseDictionary(new JaysonDeserializationContext
				{
					Text = str,
					Length = str.Length,
					Settings = settings ?? JaysonDeserializationSettings.Default
				});

			if (result != null)
			{
				object typesObj;
				if (result.TryGetValue("$types", out typesObj))
				{
					result.TryGetValue("$value", out typesObj);
					result = typesObj as IDictionary<string, object>;
				}
			}

			return result;
		}

		public static T ToObject<T>(string str, JaysonDeserializationSettings settings = null)
		{
			if (String.IsNullOrEmpty(str))
			{
				return default(T);
			}
			return (T)ToObject(str, typeof(T), settings);
		}

		public static object ToObject(string str, JaysonDeserializationSettings settings = null)
		{
			return ToObject(str, typeof(object), settings);
		}

		public static object ToObject(string str, Type toType, JaysonDeserializationSettings settings = null)
		{
			if (String.IsNullOrEmpty(str))
			{
				return JaysonTypeInfo.GetDefault(toType);
			}

			var context = new JaysonDeserializationContext
			{
				Text = str,
				Length = str.Length,
				Position = 0,
				Settings = settings ?? JaysonDeserializationSettings.Default
			};

			object result = Parse(context);

			var dResult = result as IDictionary<string, object>;
			if (dResult != null)
			{
				object typesObj;
				if (dResult.TryGetValue("$types", out typesObj))
				{
					context.GlobalTypes = new JaysonDeserializationTypeList(typesObj as IDictionary<string, object>);
					dResult.TryGetValue("$value", out result);
				}
			}

			if (result == null)
			{
				return JaysonTypeInfo.GetDefault(toType);
			}

			var instanceType = result.GetType();

			if (!context.HasTypeInfo)
			{
				if (toType == instanceType ||
					toType == typeof(object) ||
					toType.IsAssignableFrom(instanceType))
				{
					return result;
				}
				return ConvertObject(result, toType, context);
			}

			var toInfo = JaysonTypeInfo.GetTypeInfo(toType);
			var instanceInfo = JaysonTypeInfo.GetTypeInfo(instanceType);

			if (toInfo.JPrimitive)
			{
				if (toInfo.Type == instanceInfo.Type)
				{
					return result;
				}

				if (context.HasTypeInfo)
				{
					IDictionary<string, object> primeDict = result as IDictionary<string, object>;
					if (primeDict != null)
					{
						return ConvertDictionary(primeDict, toType, context, true);
					}
				}

				bool converted;
				return JaysonCommon.ConvertToPrimitive(result, toType, out converted);
			}

			bool asReadOnly = false;
			if (result is IList<object>)
			{
				bool asList, asArray;
				Type listType = GetEvaluatedListType(toType, out asList, out asArray, out asReadOnly);

				result = ConvertList((IList<object>)result, listType, context);
				if (result == null)
				{
					return result;
				}

				Type resultType = result.GetType();
				if (toType == resultType ||
					toType.IsAssignableFrom(resultType))
				{
					return result;
				}

				if (asReadOnly)
				{
					return GetReadOnlyCollectionActivator(toType)(new object[] { result });
				}
				return result;
			}

			if (result is IDictionary<string, object>)
			{
				bool asDictionary;
				var dictionaryType = GetEvaluatedDictionaryType(toType, out asDictionary, out asReadOnly);
				result = ConvertDictionary((IDictionary<string, object>)result, dictionaryType, context, true);
			}
			else if (result is IDictionary)
			{
				bool asDictionary;
				var dictionaryType = GetEvaluatedDictionaryType(toType, out asDictionary, out asReadOnly);
				result = ConvertDictionary((IDictionary<string, object>)result, dictionaryType, context);
			}
			else if (result is IList)
			{
				result = ConvertList((IList<object>)result, toType, context);
			}
			else
			{
				result = ConvertObject(result, toType, context);
			}

			if (result == null)
			{
				return result;
			}

			Type resultTypeD = result.GetType();
			if (toType == resultTypeD ||
				toType.IsAssignableFrom(resultTypeD))
			{
				return result;
			}

			#if !(NET4000 || NET3500 || NET3000 || NET2000)
			if (asReadOnly)
			{
				return GetReadOnlyDictionaryActivator(toType)(new object[] { result });
			}
			#endif

			throw new JaysonException("Unable to cast result to expected type.");
		}

		#if !(NET3500 || NET3000 || NET2000)
		public static dynamic ToDynamic(string str)
		{
			return ToExpando(str);
		}

		public static ExpandoObject ToExpando(string str, JaysonDeserializationSettings settings = null)
		{
			if (settings == null)
			{
				settings = new JaysonDeserializationSettings();
				JaysonDeserializationSettings.Default.AssignTo(settings);
				settings.DictionaryType = DictionaryDeserializationType.Expando;
			}
			else if (settings.DictionaryType != DictionaryDeserializationType.Expando)
			{
				var temp = new JaysonDeserializationSettings();
				settings.AssignTo(temp);
				temp = settings;
				settings.DictionaryType = DictionaryDeserializationType.Expando;
			}

			var result = ToDictionary(str, settings);

			if (result != null)
			{
				object typesObj;
				if (result.TryGetValue("$types", out typesObj))
				{
					result.TryGetValue("$value", out typesObj);
					result = typesObj as IDictionary<string, object>;
				}
			}

			return (ExpandoObject)result;
		}
		#endif

		# endregion ToX
	}

	# endregion JsonConverter
}
