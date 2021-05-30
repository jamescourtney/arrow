// Licensed to the Apache Software Foundation (ASF) under one or more
// contributor license agreements. See the NOTICE file distributed with
// this work for additional information regarding copyright ownership.
// The ASF licenses this file to You under the Apache License, Version 2.0
// (the "License"); you may not use this file except in compliance with
// the License.  You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using Flatbuf = org.apache.arrow.flatbuf;

namespace Apache.Arrow.Ipc
{
    internal class MessageSerializer
    {
        public const int IpcContinuationToken = -1;

        public static Types.NumberType GetNumberType(int bitWidth, bool signed)
        {
            if (signed)
            {
                if (bitWidth == 8)
                    return Types.Int8Type.Default;
                if (bitWidth == 16)
                    return Types.Int16Type.Default;
                if (bitWidth == 32)
                    return Types.Int32Type.Default;
                if (bitWidth == 64)
                    return Types.Int64Type.Default;
            }
            else
            {
                if (bitWidth == 8)
                    return Types.UInt8Type.Default;
                if (bitWidth == 16)
                    return Types.UInt16Type.Default;
                if (bitWidth == 32)
                    return Types.UInt32Type.Default;
                if (bitWidth == 64)
                    return Types.UInt64Type.Default;
            }
            throw new Exception($"Unexpected bit width of {bitWidth} for " +
                                $"{(signed ? "signed " : "unsigned")} integer.");
        }

        internal static Schema GetSchema(Flatbuf.Schema schema)
        {
            List<Field> fields = new List<Field>();
            for (int i = 0; i < schema.fields.Count; i++)
            {
                Flatbuf.Field field = schema.fields[i];

                fields.Add(FieldFromFlatbuffer(field));
            }

            Dictionary<string, string> metadata = null;
            int? metadataCount = schema.custom_metadata?.Count;
            if (metadataCount > 0)
            {
                metadata = new Dictionary<string, string>(metadataCount.Value);
                foreach (Flatbuf.KeyValue keyValue in schema.custom_metadata)
                {
                    metadata[keyValue.key] = keyValue.value;
                }
            }

            return new Schema(fields, metadata, copyCollections: false);
        }

        private static Field FieldFromFlatbuffer(Flatbuf.Field flatbufField)
        {
            IList<Flatbuf.Field> fbChildFields = flatbufField.children;
            Field[] childFields = null;

            if (fbChildFields != null)
            {
                childFields = new Field[fbChildFields.Count];

                for (int i = 0; i < childFields.Length; i++)
                {
                    Flatbuf.Field childFlatbufField = fbChildFields[i];
                    childFields[i] = FieldFromFlatbuffer(childFlatbufField);
                }
            }

            Dictionary<string, string> metadata = null;
            IList<Flatbuf.KeyValue> fbCustomMetadata = flatbufField.custom_metadata;
            if (fbCustomMetadata != null)
            {
                foreach (Flatbuf.KeyValue keyValue in fbCustomMetadata)
                {
                    metadata[keyValue.key] = keyValue.value;
                }
            }

            return new Field(flatbufField.name, GetFieldArrowType(flatbufField, childFields), flatbufField.nullable, metadata, copyCollections: false);
        }

        private static Types.IArrowType GetFieldArrowType(
            Flatbuf.Field field,
            Field[] childFields = null)
        {
            return field.type.Switch<Types.IArrowType>(
                caseInt: v => MessageSerializer.GetNumberType(v.bitWidth, v.isSigned),
                caseFloatingPoint: new Func<Flatbuf.FloatingPoint, Types.IArrowType>(v =>
                {
                    switch (v.precision)
                    {
                        case Flatbuf.Precision.SINGLE:
                            return Types.FloatType.Default;
                        case Flatbuf.Precision.DOUBLE:
                            return Types.DoubleType.Default;
                        case Flatbuf.Precision.HALF:
                            return Types.HalfFloatType.Default;
                        default:
                            throw new InvalidDataException("Unsupported floating point precision");
                    }
                }),
                caseBool: v => new Types.BooleanType(),
                caseDecimal: new Func<Flatbuf.Decimal, Types.IArrowType>(v =>
                {
                    switch (v.bitWidth)
                    {
                        case 128:
                            return new Types.Decimal128Type(v.precision, v.scale);
                        case 256:
                            return new Types.Decimal256Type(v.precision, v.scale);
                        default:
                            throw new InvalidDataException("Unsupported decimal bit width " + v.bitWidth);
                    }
                }),
                caseDate: new Func<Flatbuf.Date, Types.IArrowType>(v =>
                {
                    switch (v.unit)
                    {
                        case Flatbuf.DateUnit.DAY:
                            return Types.Date32Type.Default;
                        case Flatbuf.DateUnit.MILLISECOND:
                            return Types.Date64Type.Default;
                        default:
                            throw new InvalidDataException("Unsupported date unit");
                    }
                }),
                caseTime: new Func<Flatbuf.Time, Types.IArrowType>(v =>
                {
                    switch (v.bitWidth)
                    {
                        case 32:
                            return new Types.Time32Type(v.unit.ToArrow());
                        case 64:
                            return new Types.Time64Type(v.unit.ToArrow());
                        default:
                            throw new InvalidDataException("Unsupported time bit width");
                    }
                }),
                caseTimestamp: v =>
                {
                    Types.TimeUnit unit = v.unit.ToArrow();
                    string timezone = v.timezone;
                    return new Types.TimestampType(unit, timezone);
                },
                caseInterval: v => new Types.IntervalType(v.unit.ToArrow()),
                caseUtf8: v => new Types.StringType(),
                caseBinary: v => Types.BinaryType.Default,
                caseList: v =>
                {
                    if (childFields == null || childFields.Length != 1)
                    {
                        throw new InvalidDataException($"List type must have exactly one child.");
                    }
                    return new Types.ListType(childFields[0]);
                },
                caseStruct_: v =>
                {
                    Debug.Assert(childFields != null);
                    return new Types.StructType(childFields);
                },
                caseDefault: () => throw new InvalidDataException($"Arrow primitive '{field.TypeType}' is unsupported.")
            );
        }
    }
}
