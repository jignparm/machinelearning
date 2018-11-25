// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.Internal.Utilities;
//using Microsoft.ML.Scoring;
using Microsoft.ML.OnnxRuntime;
using System.Numerics.Tensors;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.StaticPipe;

using OnnxShape = System.Collections.Generic.List<int>;
using Microsoft.ML.Data;

namespace Microsoft.ML.Transforms
{
    /// <summary>
    /// OnnxModel is a utility class to load ONNX models, and retrieve metadata
    /// for inputs and outputs. The metadata includes the names, shapes and types
    /// It provides API to open a session, score tensors (NamedOnnxValues) and return
    /// the results.
    /// </summary>
    public sealed class OnnxModel
    {

        /// <summary>
        /// OnnxModelInfo contains the data that we should get from
        /// Sonoma API once that functionality is added.
        /// </summary>
        public sealed class OnnxModelInfo
        {
            public readonly OnnxNodeInfo[] InputsInfo;
            public readonly OnnxNodeInfo[] OutputsInfo;

            public OnnxModelInfo(OnnxNodeInfo[] inputsInfo, OnnxNodeInfo[] outputsInfo)
            {
                InputsInfo = inputsInfo;
                OutputsInfo = outputsInfo;
            }
        }

        /// <summary>
        /// OnnxNodeInfo contains all the information for a given node (e.g. inputs/outputs)
        /// of an Onnx model.
        /// </summary>
        public class OnnxNodeInfo
        {
            /// <summary>
            /// The Name of the input node
            /// </summary>
            public readonly string Name;
            /// <summary>
            /// The shape of the input node
            /// </summary>
            public readonly OnnxShape Shape;
            /// <summary>
            /// The type of the input node
            /// </summary>
            public readonly System.Type Type;

            public OnnxNodeInfo(string name, OnnxShape shape, System.Type type)
            {
                Name = name;
                Shape = shape;
                Type = type;
            }
        }

        public readonly OnnxModelInfo ModelInfo;
        private readonly InferenceSession _session;
        private readonly string _modelFile;
        public readonly List<string> InputNames;
        public readonly List<string> OutputNames;

        public OnnxModel(string modelFile)
        {
            _modelFile = modelFile;
            _session = new InferenceSession(modelFile);
            ModelInfo = new OnnxModelInfo(GetInputsInfo(), GetOutputsInfo());
            InputNames = ModelInfo.InputsInfo.Select(i => i.Name).ToList();
            OutputNames = ModelInfo.OutputsInfo.Select(i => i.Name).ToList();
        }

        /// <summary>
        /// Create an OnnxModel from a byte[]
        /// </summary>
        /// <param name="modelBytes"></param>
        /// <returns>OnnxModel</returns>
        public static OnnxModel CreateFromBytes(byte[] modelBytes)
        {
            var tempModelDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempModelDir);

            var tempModelFile = Path.Combine(tempModelDir, "model.onnx");
            File.WriteAllBytes(tempModelFile, modelBytes);
            return new OnnxModel(tempModelFile);

            // TODO:
            // tempModelFile is needed in case the model needs to be saved
            // Either have to save the modelbytes and delete the temp dir/file,
            // or keep the dir/file and write proper cleanup when application closes
        }

        /// <summary>
        /// Uses an already open session to score a list of Tensors/NamedOnnxValues.
        /// </summary>
        /// <param name="inputTensors">The NamedOnnxValues/Tensors to score</param>
        /// <returns>A list of NamedOnnxValues/Tensors</returns>
        public List<NamedOnnxValue> Run(List<NamedOnnxValue> inputTensors)
        {
            var outputTensors = _session.Run(inputTensors);
            var results = new List<NamedOnnxValue>();
            foreach (var tensor in outputTensors)
            {
                results.Add(tensor);
            }
            return results;
        }

        /// <summary>
        /// Convert the model to a byte array.
        /// </summary>
        /// <returns>byte[]</returns>
        public byte[] ToByteArray()
        {
            return File.ReadAllBytes(_modelFile);
        }

        /// <summary>
        /// Returns input metadata of the ONNX model.
        /// </summary>
        /// <returns>OnnxNodeInfo[]</returns>
        public OnnxNodeInfo[] GetInputsInfo()
        {
            var nodeInfos = new List<OnnxNodeInfo>();
            var inputMeta = _session.InputMetadata;
            foreach (var kv in inputMeta)
            {
                nodeInfos.Add( new OnnxNodeInfo(kv.Key, kv.Value.Dimensions.ToList(), kv.Value.Type));
            }
            return nodeInfos.ToArray();
        }

        /// <summary>
        /// Returns output metadata of the ONNX model.
        /// </summary>
        /// <returns></returns>
        public OnnxNodeInfo[] GetOutputsInfo()
        {
            var nodeInfos = new List<OnnxNodeInfo>();
            var outputMeta = _session.OutputMetadata;
            foreach (var kv in outputMeta)
            {
                nodeInfos.Add(new OnnxNodeInfo(kv.Key, kv.Value.Dimensions.ToList(), kv.Value.Type));
            }
            return nodeInfos.ToArray();
        }
    }

    internal sealed class OnnxUtils
    {
        private static Dictionary<System.Type, System.Type> _onnxTypeMap;
        private static Dictionary<System.Type, DataKind> _typeToKindMap;

        /// <summary>
        /// Creates a Tensor from a scalar value.
        /// </summary>
        /// <typeparam name="T">The type of the Tensor.</typeparam>
        /// <param name="data">The data values of the Tensor</param>
        /// <returns>An object which can be cast to an Tensor. The shape of the Tensor is not filled.</returns>
        public static Object CreateScalarTensor<T>(T data)
        {
            var typeMap = SystemTypeToOnnxType();
            if (!typeMap.ContainsKey(typeof(T)))
                throw new NotImplementedException($"Not implemented type {typeof(T)}");
            return new DenseTensor<T>(new T[] { data }, new int[] { });
        }

        /// <summary>
        /// Create a Tensor from vbuffer span. Checks if the tensor type
        /// is supported by OnnxRuntime prior to execution.
        /// </summary>
        /// <typeparam name="T">The type of Tensor to create.</typeparam>
        /// <param name="data">A span containing the data.</param>
        /// <param name="shape">The shape of the tensor being created</param>
        /// <returns></returns>
        public static Object CreateTensor<T>(ReadOnlySpan<T> data, OnnxShape shape)
        {
            var typeMap = SystemTypeToOnnxType();
            if (!typeMap.ContainsKey(typeof(T)))
                throw new NotImplementedException($"Not implemented type {typeof(T)}");
            return new DenseTensor<T>(data.ToArray(), shape.Select(x => (int)x).ToArray());
        }

        /// <summary>
        /// Copies a Tensor to a Span element by element.
        /// </summary>
        /// <typeparam name="T">The type of both Span and Tensor</typeparam>
        /// <param name="tensor">The source tensor</param>
        /// <param name="dst">The destination span</param>
        public static unsafe void CopyTo<T>(Tensor<T> tensor, Span<T> dst)
        {
            for (int i = 0; i < tensor.Length; i++)
            {
                dst[i] = tensor.GetValue(i);
            }
            GC.KeepAlive(tensor);
        }

        /// <summary>
        /// Converts a Onnx type, that follows the System.Type convention
        /// to the type system ML.NET recognizes (e.g. I4, I8, R4 etc.)
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static PrimitiveType OnnxToMlNetType(System.Type type)
        {
            var map = OnnxToMlNetTypeMap();
            if (!map.ContainsKey(type))
               throw Contracts.ExceptNotSupp("Onnx type not supported", type);
            return PrimitiveType.FromKind(map[type]);
        }

        internal static Dictionary<System.Type, DataKind> OnnxToMlNetTypeMap()
        {
            if (_typeToKindMap == null)
            {
                _typeToKindMap = new Dictionary<System.Type, DataKind>
                {
                    { typeof(Single) , DataKind.R4},
                    { typeof(Double) , DataKind.R8},
                    { typeof(Int16) , DataKind.I2},
                    { typeof(Int32) , DataKind.I4},
                    { typeof(Int64) , DataKind.I8},
                    { typeof(UInt16) , DataKind.U2},
                    { typeof(UInt32) , DataKind.U4},
                    { typeof(UInt64) , DataKind.U8},
                    { typeof(String) , DataKind.TX},
                    { typeof(Boolean) , DataKind.BL},
                };
            }
            return _typeToKindMap;
        }

        internal static Dictionary<System.Type, System.Type> SystemTypeToOnnxType()
        {
            if (_onnxTypeMap == null)
            {
                _onnxTypeMap = new Dictionary<System.Type, System.Type>
                {
                    { typeof(Double) , typeof(Double) },
                    { typeof(Single) , typeof(Single) },
                    { typeof(Int16) , typeof(Int16) },
                    { typeof(Int32) , typeof(Int32) },
                    { typeof(Int64) , typeof(Int64) },
                    { typeof(UInt16) , typeof(UInt16) },
                    { typeof(UInt32) , typeof(UInt32) },
                    { typeof(UInt64) , typeof(UInt64) }
                };
            }
            return _onnxTypeMap;
        }
    }
}
