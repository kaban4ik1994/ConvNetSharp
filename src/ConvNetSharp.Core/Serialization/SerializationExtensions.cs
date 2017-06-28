﻿using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConvNetSharp.Core.Serialization
{
    public static class SerializationExtensions
    {
        public static Net<T> FromJson<T>(string json) where T : struct, IEquatable<T>, IFormattable
        {
            var data = JsonConvert.DeserializeObject<JObject>(json);
            var dico = data.ToDictionary();
            var net = Net<T>.FromData(dico);
            return net;
        }

        public static T[] ToArrayOfT<T>(this object obj)
        {
            var arrayofT = obj as T[];
            if (arrayofT != null)
            {
                return arrayofT;
            }

            var jarray = obj as JArray;
            if (jarray != null)
            {
                return jarray.ToObject<T[]>();
            }

            return ((object[])obj).Cast<T>().ToArray();
        }

        public static string ToJson<T>(this Net<T> net) where T : struct, IEquatable<T>, IFormattable
        {
            var data = net.GetData();
            var json = JsonConvert.SerializeObject(data);
            return json;
        }
    }
}