﻿//////////////////////////////////////////////////////////////////////////////////////////
// ANNdotNET - Deep Learning Tool on .NET Platform                                                      //
// Copyright 2017-2018 Bahrudin Hrnjica                                                 //
//                                                                                      //
// This code is free software under the MIT License                                     //
// See license section of  https://github.com/bhrnjica/anndotnet/blob/master/LICENSE.md  //
//                                                                                      //
// Bahrudin Hrnjica                                                                     //
// bhrnjica@hotmail.com                                                                 //
// Bihac, Bosnia and Herzegovina                                                         //
// http://bhrnjica.net                                                                  //
//////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using CNTK;
using NNetwork.Core;
using NNetwork.Core.Common;
using System.Globalization;

namespace ANNdotNET.Core
{
    /// <summary>
    /// Custom implementation of the Minibatch-source which support unique way of feeding with data, during training, validation and evaluation of the model.
    /// Mostly the class implements sequences with variables size which is not supported by default CNTK implementation of FileText based Minibatch source. 
    /// </summary>
    public class MinibatchSourceEx : IDisposable
    {

        #region Ctor and private members
        public static readonly string m_NormalizedSufixName = "_norm";
        private MinibatchSource defaultmb;
        private StreamReader custommb;

        public MinibatchSourceEx(MinibatchType type, StreamConfiguration[] streamConfigurations, List<Variable> inputVar, List<Variable> outputVar,
            string trainFilePath, string validFilePath, ulong epochSize, bool randomizeBatch, int useImgAugm)
        {
            this.StreamConfigurations = streamConfigurations;
            this.TrainingDataFile = trainFilePath;
            this.ValidationDataFile = validFilePath;
            Type = type;

            if (Type == MinibatchType.Default)
                // prepare the training data
                defaultmb = MinibatchSource.TextFormatMinibatchSource(trainFilePath, StreamConfigurations, epochSize, randomizeBatch);
            else if (Type == MinibatchType.Image)
            {
                var featVar = inputVar.First();
                //
                int image_width = featVar.Shape.Dimensions[0];
                int image_height = featVar.Shape.Dimensions[1];
                int num_channels = featVar.Shape.Dimensions[2];

                //make transformation and scaling
                var transforms = new List<CNTKDictionary>();
                var randomSideTransform = CNTKLib.ReaderCrop("RandomSide",
                      new Tuple<int, int>(0, 0),
                      new Tuple<float, float>(0.8f, 1.0f),
                      new Tuple<float, float>(0.0f, 0.0f),
                      new Tuple<float, float>(1.0f, 1.0f),
                      "uniRatio");
                if(useImgAugm == 1)
                    transforms.Add(randomSideTransform);

                //scaling image comes at the end of image transformation
                var scaleTransform = CNTKLib.ReaderScale(image_width, image_height, num_channels);
                transforms.Add(scaleTransform);


                var labelName = streamConfigurations.Last().m_streamName;
                var labelDimension = streamConfigurations.Last().m_dim;
                var featureName = streamConfigurations.First().m_streamName;
                var imagemb = CNTKLib.ImageDeserializer(trainFilePath, labelName,(uint)labelDimension, featureName, transforms);
                var mmsConfig = new CNTK.MinibatchSourceConfig(new CNTK.DictionaryVector() { imagemb });

                //
                defaultmb = CNTKLib.CreateCompositeMinibatchSource(mmsConfig);
            }
            else if (Type == MinibatchType.Speech)
            {
                //fd = HTKFeatureDeserializer(StreamDefs(
                //    amazing_features = StreamDef(shape = feature_dim, context = (context, context), scp = features_file)))

                //ld = HTKMLFDeserializer(label_mapping_file, StreamDefs(
                //    awesome_labels = StreamDef(shape = num_classes, mlf = labels_file)))

                //# Enabling BPTT with truncated_length > 0
                //            return MinibatchSource([fd, ld], truncation_length = truncation_length, max_samples = total_number_of_samples)

                var scp_file = "";//glob_0000.scp
                var labelMappingFile = "";//state.list
                var mlfFile = "";//glob_0000.mlf
                //
                var featVar = inputVar.First();
                var labelName = streamConfigurations.Last().m_streamName;
                var labelDimension = streamConfigurations.Last().m_dim;
                var featureName = streamConfigurations.First().m_streamName;
                var featDim = streamConfigurations.First().m_dim;
                var fc = new HTKFeatureConfiguration(featureName, scp_file, featDim,0,0,false);
                var htkconfig = new HTKFeatureConfigurationVector() { fc };

                var fconfig = CNTKLib.HTKFeatureDeserializer(htkconfig);
                var lconfig = CNTKLib.HTKMLFDeserializer(featureName,labelMappingFile, labelDimension,new StringVector() { mlfFile });
                var mmsConfig = new CNTK.MinibatchSourceConfig(new CNTK.DictionaryVector() { fconfig,lconfig });

                //
                defaultmb = CNTKLib.CreateCompositeMinibatchSource(mmsConfig);
            }
            else if (Type == MinibatchType.Custom)
                custommb = new StreamReader(trainFilePath);
            else
                throw new Exception("Minibatchsource type is unknown!");
        }
        
        #endregion

        #region Public Members

        public StreamConfiguration[] StreamConfigurations { get; private set; }
        public string TrainingDataFile { get; private set; }
        public string ValidationDataFile { get; private set; }
        public MinibatchType Type { get; private set; }


        /// <summary>
        /// Public implementation of Dispose pattern callable by consumers.
        /// </summary>
        public void Dispose()
        {
            if (defaultmb != null)
                defaultmb.Dispose();
            if (custommb != null)
                custommb.Dispose();
        }

        /// <summary>
        /// Protected implementation of Dispose pattern.
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {

            if (disposing)
            {
                Dispose();
            }
        }

        /// <summary>
        /// The method is called during training process. The method returns the chunk of data specified by Batch size.
        /// </summary>
        /// <param name="minibatchSizeInSamples"></param>
        /// <param name="device"></param>
        /// <returns></returns>
        public UnorderedMapStreamInformationMinibatchData GetNextMinibatch(uint minibatchSizeInSamples, DeviceDescriptor device)
        {
            if (Type == MinibatchType.Default || Type == MinibatchType.Image)
            {
                var retVal = defaultmb.GetNextMinibatch(minibatchSizeInSamples, device);
                return retVal;
            }
            else if (Type == MinibatchType.Custom)
            {
                var retVal = nextBatch(custommb, StreamConfigurations, (int)minibatchSizeInSamples);
                var mb = new UnorderedMapStreamInformationMinibatchData();
                var eofs = custommb.EndOfStream;
                //create minibatch
                foreach (var d in retVal)
                {
                    var v = Value.CreateBatchOfSequences<float>(new NDShape(1, d.Key.m_dim), d.Value, device);
                    var mbd = new MinibatchData(v, (uint)d.Value.Count(), (uint)d.Value.Sum(x => x.Count), eofs);

                    var si = new StreamInformation();
                    si.m_definesMbSize = d.Key.m_definesMbSize;
                    si.m_storageFormat = StorageFormat.Dense;
                    si.m_name = d.Key.m_streamName;

                    mb.Add(si, mbd);
                }

                return mb;
            }
            else
                throw new Exception("Unsupported Mini-batch-source type!");

        }
        public Dictionary<Variable, Value> GetNextMinibatch(uint minibatchSizeInSamples, ref bool sweepEnd, List<Variable> vars,  DeviceDescriptor device)
        {
            if (Type == MinibatchType.Default || Type == MinibatchType.Image || Type == MinibatchType.Speech)
            {
                var args = defaultmb.GetNextMinibatch(minibatchSizeInSamples, device);
                sweepEnd = args.Any(x => x.Value.sweepEnd);
                //
                var arguments = MinibatchSourceEx.ToMinibatchValueData(args, vars);
                return arguments;
            }
            else if (Type == MinibatchType.Custom)
            {
                var retVal = nextBatch(custommb, StreamConfigurations, (int)minibatchSizeInSamples);
                var mb = new Dictionary<Variable, Value>();
                sweepEnd = custommb.EndOfStream;
                //create minibatch
                foreach (var d in retVal)
                {
                    var v = Value.CreateBatchOfSequences<float>(new NDShape(1, d.Key.m_dim), d.Value, device);
                    //
                    var var = vars.Where(x => x.Name == d.Key.m_streamName).FirstOrDefault();
                    if (var == null)
                        throw new Exception("Variable cannot be  null!");
                    //
                    mb.Add(var, v);
                }
                return mb;
            }
            else
                throw new Exception("Unsupported Mini-batch-source type!");

        }

        /// <summary>
        /// When using Normalization of the input variables, before training process and network creation,
        /// we must calculated mean and standard deviation in order to prepare the normalization layer during network creation
        /// </summary>
        /// <param name="inputVars"></param>
        /// <param name="device"></param>
        /// <returns></returns>
        public List<Function> NormalizeInput(List<Variable> inputVars, DeviceDescriptor device)
        {
            if (inputVars.Count>0 && Type != MinibatchType.Default)
                throw new Exception("Input normalization is supported for default minibatch source only!");


            var globalMeanStd = new Dictionary<StreamInformation, Tuple<NDArrayView, NDArrayView>>();
            foreach (var var in inputVars)
            {
                var inputMeansAndInvStdDevs = new Dictionary<StreamInformation, Tuple<NDArrayView, NDArrayView>>();
                var featureStreamInfo = defaultmb.StreamInfo(var.Name);
                inputMeansAndInvStdDevs.Add(featureStreamInfo, new Tuple<NDArrayView, NDArrayView>(null, null));

                //compute mean and standard deviation of the population for inputs variables
                MinibatchSource.ComputeInputPerDimMeansAndInvStdDevs(defaultmb, inputMeansAndInvStdDevs, device);

                //add to global variable
                var v = inputMeansAndInvStdDevs.First();
                //var avg = (new Value(v.Value.Item1)).GetDenseData<float>(var);
                //var std = (new Value(v.Value.Item2)).GetDenseData<float>(var);
                globalMeanStd.Add(v.Key, v.Value);
            }

            //
            var normalizedInputs = new List<Function>();
            foreach (var input in inputVars)
            {
                var z = globalMeanStd.Where(x => x.Key.m_name == input.Name).Select(x => x.Value).FirstOrDefault();

                var featureStreamInfo = defaultmb.StreamInfo(input.Name);

                //
                var mean = new Constant(z.Item1, "mean");
                var std = new Constant(z.Item2, "std");

                //
                var normalizedinput = CNTKLib.PerDimMeanVarianceNormalize(input, mean, std, input.Name + m_NormalizedSufixName);
                //
                normalizedInputs.Add(normalizedinput);

            }

            return normalizedInputs;
        }

        /// <summary>
        /// The method is called during Evaluation of the model for specific data set which is specified as an argument
        /// </summary>
        /// <param name="type"></param>
        /// <param name="strFilePath">dataset file path</param>
        /// <param name="streamConfigurations">stream configuration which provides meta-data information</param>
        /// <param name="device"></param>
        /// <returns></returns>
        public static UnorderedMapStreamInformationMinibatchData GetFullBatch(MinibatchType type, string strFilePath, StreamConfiguration[] streamConfigurations, DeviceDescriptor device)
        {
            if (type == MinibatchType.Default)
            {
                var mbs = MinibatchSource.TextFormatMinibatchSource(strFilePath, streamConfigurations, MinibatchSource.FullDataSweep, false);
                //
                var minibatchData = mbs.GetNextMinibatch(int.MaxValue, device);
                //
                return minibatchData;
            }
            else if (type == MinibatchType.Custom)
            {
                using (var mbreader = new StreamReader(strFilePath))
                {
                    var retVal = nextBatch1(mbreader, streamConfigurations, -1, 1, device);
                    var mb = new UnorderedMapStreamInformationMinibatchData();

                    for (int i = 0; i < retVal.Count; i++)
                    {
                        var k = retVal.ElementAt(i);
                        
                        var key = k.Key;
                        var si = new StreamInformation();
                        si.m_definesMbSize = streamConfigurations[i].m_definesMbSize;
                        si.m_storageFormat = k.Value.StorageFormat;
                        si.m_name = streamConfigurations[i].m_streamName;
                        
                        var stream = streamConfigurations[i];
                        mb.Add(si,new MinibatchData( k.Value));
                    }

                    return mb;
                }
            }
            else
                throw new Exception("Minibatch is not supported.");

        }


        /// <summary>
        /// Convert minibatch data to Value 
        /// </summary>
        /// <param name="args"> data being converted</param>
        /// <returns></returns>
        public static Dictionary<Variable, Value> ToMinibatchValueData(UnorderedMapStreamInformationMinibatchData args, List<Variable> vars)
        {
            var arguments = new Dictionary<Variable, Value>();
            foreach (var mbd in args)
            {
                var v = vars.Where(x => x.Name == mbd.Key.m_name).FirstOrDefault();
                if (v == null)
                    throw new Exception("Stream is invalid!");

                arguments.Add(v, mbd.Value.data.DeepClone(true));
            }
            return arguments;
        }
        #endregion

        #region Private Methods
        /// <summary>
        /// Helper method for retrieving the next batch of data for the custom minibatch source.
        /// </summary>
        /// <param name="stream">Stream object</param>
        /// <param name="m_streamConfig"></param>
        /// <param name="batchSize"></param>
        /// <param name="iteration"></param>
        /// <param name="device"></param>
        /// <returns></returns>
        private static Dictionary<StreamConfiguration, List<List<float>>> nextBatch(TextReader stream,
            StreamConfiguration[] m_streamConfig, int batchSize)
        {
            var values = new Dictionary<StreamConfiguration, List<List<float>>>();
            
            //local function for creating a batch of data
            if (((StreamReader)stream).EndOfStream)
                ((StreamReader)stream).BaseStream.Position = 0;

            //in case batchSize is less than 1 retrieve all data set
            var reader = batchSize <= 0 ? ReadLineFromFile((StreamReader)stream) : ReadLineFromFile((StreamReader)stream).Take(batchSize);
            //
            foreach (var batchLine in reader)
            {
                var streams = batchLine.Split(MLFactory.m_cntkSpearator, StringSplitOptions.RemoveEmptyEntries);
                var dics = processTextLine<float>(streams, m_streamConfig);
                //
                foreach (var d in dics)
                {
                    if (!values.ContainsKey(d.Key))
                    {
                        var l = new List<List<float>>();
                        l.Add(d.Value);
                        values.Add(d.Key, l);
                    }
                    else
                        values[d.Key].Add(d.Value);
                }
            }
            

            //in case of end batch return null
            //this should never happen
            if (values.Count == 0)
            {
                return null;
            }
            else
                return values;
        }
        private static Dictionary<StreamConfiguration, Value> nextBatch1(TextReader stream,
           StreamConfiguration[] m_streamConfig, int batchSize, int iteration, DeviceDescriptor device)
        {
            var values = new Dictionary<StreamConfiguration, List<List<float>>>();
            var retVal = new Dictionary<StreamConfiguration, Value>();
            var endOfStream = false;

            //local function for creating a batch of data
            if (((StreamReader)stream).EndOfStream)
                ((StreamReader)stream).BaseStream.Position = 0;

            //in case batchSize is less than 1 retrieve all data set
            var reader = batchSize <= 0 ? ReadLineFromFile((StreamReader)stream) : ReadLineFromFile((StreamReader)stream).Take(batchSize);
            //
            foreach (var batchLine in reader)
            {
                var streams = batchLine.Split(MLFactory.m_cntkSpearator, StringSplitOptions.RemoveEmptyEntries);
                var dics = processTextLine<float>(streams, m_streamConfig);
                //
                foreach (var d in dics)
                {
                    if (!values.ContainsKey(d.Key))
                    {
                        var l = new List<List<float>>();
                        l.Add(d.Value);
                        values.Add(d.Key, l);
                    }
                    else
                        values[d.Key].Add(d.Value);
                }
            }
            //check for end of file
            endOfStream = ((StreamReader)stream).EndOfStream;

            //in case of end batch return null
            //this should never happen
            if (values.Count == 0)
            {
                return null;
            }

            //create minibatch
            foreach (var d in values)
            {
                var v = Value.CreateBatchOfSequences<float>(new NDShape(1, d.Key.m_dim), d.Value, device);
                //var mbd = new MinibatchData(v, (uint)d.Value.Count, (uint)d.Value.Sum(x => x.Count), endOfStream);
                //retVal.Add(d.Key, mbd);
                retVal.Add(d.Key, v);
            }
            //
            return retVal;
        }

        /// <summary>
        /// process and parse one line of text data file
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="streams"></param>
        /// <param name="m_streamConfig"></param>
        /// <returns></returns>
        private static Dictionary<StreamConfiguration, List<float>> processTextLine<T>(string[] streams, StreamConfiguration[] m_streamConfig)
        {
            var retVal = new Dictionary<StreamConfiguration, List<float>>();
            foreach (var stream in m_streamConfig)
            {
                var strvalues = streams.Where(x => x.StartsWith(stream.m_streamName)).Select(x => x.Substring(stream.m_streamName.Length)).FirstOrDefault();
                if (strvalues != null)
                {
                    var lst = strvalues.Split(MLFactory.m_cntkSpearator2, StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToList();
                    retVal.Add(stream, lst);
                }
                else
                    return null;
            }
            return retVal;
        }

        /// <summary>
        /// the method reads the line from the Stream and returns enumeration of the strings
        /// </summary>
        /// <param name="fileReader"></param>
        /// <returns></returns>
        private static IEnumerable<string> ReadLineFromFile(StreamReader fileReader)
        {
            string currentLine;
            while ((currentLine = fileReader.ReadLine()) != null)
            {
                yield return currentLine;
            }
        }
        #endregion
    }
}
