﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;


namespace CsvHandle {
    /// <summary>
    ///     CSV文件读取类
    /// </summary>
    /// <summary>
    ///     <para> </para>
    ///     可以写入的只能是属性
    ///     <para>私有属性将直接忽略,数据存在的话,会报错</para>
    ///     加了WriteValueAttribute标签的, 将会用设置的name
    ///     <para>加了WriteValueAttribute标签的, 必须有setter, 否则将会略过并报错</para>
    ///     加了NonWriteValueAttribute标签的, 将不会被写入
    ///     <para>没有加标签的, 如果有setter, 将会写入, 如果数据不存在, 会报错</para>
    ///     没有加标签的, 如果没有setter, 将会略过
    ///     <para>数组分割使用'|','|'需要转义'\|'</para>
    ///     任何字符串都会处理'\?'的转义 ('\?'->'?')
    ///     <para> </para>
    ///     建议场景加载或静止动画时多次调用
    ///     <para>CsvHelper.RefectType[T]();</para>
    ///     或单次调用
    ///     <para>CsvHelper.RefectType(class1, class2, class3,...);</para>
    ///     来提前做好反射工作
    ///     <para> </para>
    ///     var res = CsvHelper.ReadFile[MyClass](path, Encoding.GetEncoding("gb2312"));
    /// </summary>
    public sealed class CsvHelper {
        //反射信息的缓存字典
        private static Dictionary<Type, Refect> m_cacheRefects;
        private static Dictionary<Type, ICustomConvert> m_customConvert;

        private static char noteChar='#';

        //如果读取过文件,这里存放最后一次的文件开始注解
        private static List<string> m_lastFileTitleNote;
        /// <summary>
        ///     返回最后一次的文件开始注解(包含注解字符,如'#')
        /// </summary>
        public static List<List<string>> LastFileTitleNote {
            get {
                if (m_lastFileTitleNote == null || m_lastFileTitleNote.Count <= 0)
                    return null;
                return ReadValue(m_lastFileTitleNote);
            }
        }

        /// <summary>
        ///     静态构造
        /// </summary>
        static CsvHelper() {
            m_cacheRefects = new Dictionary<Type, Refect>();
            //
            var ts = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes().Where(t => t.GetInterfaces().Contains(typeof(ICustomConvert))))
                .ToArray();
            m_customConvert = new Dictionary<Type, ICustomConvert>(ts.Length);
            foreach (var t in ts)
            {
                var ic = Activator.CreateInstance(t,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, null, null) as ICustomConvert;
                var att = t.GetCustomAttribute<CustomConvertAttribute>();
                if (att!=null)
                {
                    foreach (var type in att.Types)
                    {
                        if (m_customConvert.ContainsKey(type))
                        {
                            //如果不可被覆盖,则直接使用.可被替换,就忽略这个
                            if (!att.CanReplace)
                                m_customConvert[type] = ic;
                        }else
                            m_customConvert.Add(type, ic);
                    }
                }
            }
        }

        /// <summary>
        ///     读取文件
        /// </summary>
        /// <typeparam name="T">文件中存放的类型</typeparam>
        /// <param name="fullName">文件名(包含路径)</param>
        /// <param name="encoding">编码方式,中文可能是gb2312</param>
        /// <param name="note">文件开始注解,注解的开头是什么字符(比如把第一行作为中文注解,第二行才是属性名,则第一行是#notes)</param>
        /// <returns>读取的对象集合</returns>
        public static List<T> ReadFile<T>(string fullName, Encoding encoding, char note = '#') where T : new()
        {
            noteChar = note;
            var typeT = typeof(T);
            //如果没有就反射并缓存反射信息 
            if (!m_cacheRefects.ContainsKey(typeT))
                m_cacheRefects.Add(typeT, new Refect(typeT));
            //
            var lines = new List<string>(File.ReadLines(fullName, encoding));
            m_lastFileTitleNote = new List<string>();
            while (true)
            {
                if (lines[0][0] == note || (lines[0][0] == '"' && lines[0][1] == note))
                {
                    m_lastFileTitleNote.Add(lines[0]);
                    lines.RemoveAt(0);
                }
                else
                {
                    break;
                }
            }

            //
            var names = ReadTitle(lines[0]);
            lines.RemoveAt(0);
            var values = ReadValue(lines,names);
            //
            return Serialize<T>(names, values);
        }

        /// <summary>
        ///     提前反射需要的类型并缓存
        /// </summary>
        /// <typeparam name="T">类型</typeparam>
        public static void RefectType<T>() {
            RefectType(typeof(T));
        }

        /// <summary>
        ///     提前反射需要的类型并缓存
        /// </summary>
        /// <param name="typeT">类型</param>
        private static void RefectType(params Type[] typeT) {
            foreach (var item in typeT)
                RefectType(item);
        }

        /// <summary>
        ///     提前反射需要的类型并缓存
        /// </summary>
        /// <param name="typeT">类型</param>
        private static void RefectType(Type typeT) {
            //如果没有就反射并缓存反射信息
            if (!m_cacheRefects.ContainsKey(typeT))
                m_cacheRefects.Add(typeT, new Refect(typeT));
        }

        //读取头部,即属性名
        private static List<string> ReadTitle(string titleStr) {
            return new List<string>(titleStr.Trim().Split(','));
        }

        //读取数据部分,此时不解析
        private static List<List<string>> ReadValue( List<string> lines,List<string> names = null) {
            var resList = new List<List<string>>();
            foreach (var line in lines)
            {
                if (line.StartsWith($"{noteChar}") || line.StartsWith($"\"{noteChar}"))
                    continue;
                var valueList = new List<string>();
                var datas     = line.Split(',');
                var queue     = new Queue<string>();
                foreach (var data in datas)
                {
                    if (queue.Count <= 0)
                    {
                        if (data.StartsWith("\""))
                        {
                            if (Regex.Matches(data, "\"").Count % 2 == 0)
                                valueList.Add(DelESC(data.Substring(1, data.Length - 2), '\"'));
                            else
                                queue.Enqueue(data);
                        }
                        else
                        {
                            valueList.Add(data);
                        }
                    }
                    else
                    {
                        if (Regex.Matches(data, "\"").Count % 2 == 1)
                        {
                            queue.Enqueue(data);
                            valueList.Add(DelESC(GetStringByQueue(queue, ','), '\"'));
                            queue.Clear();
                        }
                        else
                        {
                            queue.Enqueue(data);
                        }
                    }
                }

                if (names != null && names.Count != valueList.Count)
                {
                    string str = "读取到行:" + line + "\n\n";
                    str += "并解析为:";
                    for (int i = 0; i < valueList.Count; i++)
                    {
                        str += $"<{valueList[i]}>";
                    }

                    str += "\n\n";
                    str += "却与表头字段数量不一致\ntitle:";
                    for (int i = 0; i < names.Count; i++)
                    {
                        str += $"<{names[i]}>";
                    }

                    Debug.LogError(str);
                }

                resList.Add(valueList);
            }

            return resList;
        }

        //根据属性和Type序列化这些数据
        private static List<T> Serialize<T>(List<string> names, List<List<string>> values) where T : new() {
            var list = new List<T>();
            var type = typeof(T);
            if (!m_cacheRefects.ContainsKey(type))
                throw new Exception("反射属性失败,字典中不存在");
            var refect = m_cacheRefects[type];
            var pros   = new List<PropertyInfo>();
            for (var i = 0; i < names.Count; i++) {
                PropertyInfo info    = null;
                var          proName = refect.PropertyName.Find(p => p.m_name == names[i]);
                if (proName == null)
                    info = refect.Property.Find(p => p.Name == names[i]);
                else
                    info = proName.m_property;
                if (info == null)
                {
                    if (!string.IsNullOrEmpty(names[i]))
                        Debug.LogError($"未找到表头中{names[i]}对应的属性,请确认是否标记为属性!");
                }
                pros.Add(info);
            }

            for (var i = 0; i < values.Count; i++) {
                var t = new T();
                for (var j = 0; j < pros.Count; j++) SetValue(pros[j], t, values[i][j]);
                list.Add(t);
            }

            return list;
        }

        //从队列中拼接字符串并删去前后的字符
        private static string GetStringByQueue(Queue<string> queue,char esc,bool delSide=true) {
            var str = "";
            if (queue.Count <= 0)
                return "";
            str += queue.Dequeue();
            for (;queue.Count > 0;) str += esc + queue.Dequeue();
            if (delSide)
                str = str.Substring(1, str.Length - 2);
            return str;
        }

        //删去字符串中的转义
        private static string DelESC(string str,char esc) {
            var count = 0;
            var cs    = new char[str.Length];
            for (var i = 0; i < str.Length; i++, count++) {
                if (str[i] == esc) i++;
                cs[count] = str[i];
            }

            return new string(cs, 0, count);
        }

        /// <summary>
        ///     将值中的数据赋值到obj中
        /// </summary>
        /// <param name="info">字段属性</param>
        /// <param name="obj">需要被赋值的对象</param>
        /// <param name="val">字段的值</param>
        private static void SetValue(PropertyInfo info, object obj, string val)
        {
            if (info == null)
                return;
            if (m_customConvert.ContainsKey(info.PropertyType))
            {
                //处理普通类型
                var v = m_customConvert[info.PropertyType].Parse(info.PropertyType, DelESC(val, '\\').Trim());
                info.SetValue(obj, v);
            }
            else
            {
                if (info.PropertyType.IsArray)
                {
                    //处理数组
                    var vals = SpiltArray(val);
                    var eleType = info.PropertyType.GetElementType();
                    if (eleType == null)
                        return;
                    var array = Array.CreateInstance(eleType, vals.Count);
                    for (int i = 0; i < vals.Count; i++)
                    {
                        if (m_customConvert.ContainsKey(eleType))
                        {
                            var v = m_customConvert[eleType].Parse(eleType, vals[i].Trim());
                            array.SetValue(v, i);
                        }
                        else
                        {
                            Debug.LogError($"数组类型<{eleType}>未找到转换器!请按照文档实现此类型");
                        }
                    }

                    info.SetValue(obj, array);
                    return;
                }
                else
                    Debug.LogError($"类型<{info.PropertyType}>未找到转换器!请按照文档实现此类型");
            }
        }

        //裁切数组,会进行转义
        private static List<string> SpiltArray(string val)
        {
            var strs = val.Split(',');
            List<string> list=new List<string>();
            Queue<string> queue=new Queue<string>();
            for (int i = 0; i < strs.Length; i++)
            {
                if (Regex.Matches(strs[i], "\\\\").Count % 2 != 0)
                    queue.Enqueue(strs[i].Substring(0, strs[i].Length - 1));
                else
                {
                    if (queue.Count <= 0)
                    {
                        list.Add(DelESC(strs[i], '\\'));
                    }
                    else
                    {
                        queue.Enqueue(strs[i]);
                        list.Add(DelESC(GetStringByQueue(queue, ',',false), '\\'));
                        queue.Clear();
                    }
                }
            }

            return list;
        }
    }
}