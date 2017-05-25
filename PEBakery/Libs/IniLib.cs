﻿/*
    Copyright (C) 2016-2017 Hajin Jang
    Licensed under MIT License.
 
    MIT License

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Collections;
using System.Text.RegularExpressions;
using PEBakery.Helper;
using System.Threading;
using System.Collections.Concurrent;

namespace PEBakery.Lib
{
    using StringDictionary = Dictionary<string, string>;

    #region Exceptions
    /// <summary>
    /// When parsing ini file, specified key not found.
    /// </summary>
    public class IniKeyNotFoundException : Exception
    {
        public IniKeyNotFoundException() { }
        public IniKeyNotFoundException(string message) : base(message) { }
        public IniKeyNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// When parsing ini file, specified section is not found.
    /// </summary>
    public class SectionNotFoundException : Exception
    {
        public SectionNotFoundException() { }
        public SectionNotFoundException(string message) : base(message) { }
        public SectionNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// INI file is invalid
    /// </summary>
    public class InvalidIniFormatException : Exception
    {
        public InvalidIniFormatException() { }
        public InvalidIniFormatException(string message) : base(message) { }
        public InvalidIniFormatException(string message, Exception inner) : base(message, inner) { }
    }
    #endregion

    #region IniKey
    public struct IniKey
    {
        public string Section;
        public string Key;
        public string Value; // In GetKeys, this record is not used, set to null

        public IniKey(string section)
        {
            this.Section = section;
            this.Key = null;
            this.Value = null;
        }
        public IniKey(string section, string key)
        {
            this.Section = section;
            this.Key = key;
            this.Value = null;
        }
        public IniKey(string section, string key, string value)
        {
            this.Section = section;
            this.Key = key;
            this.Value = value;
        }
    }

    public class IniKeyComparer : IComparer
    {
        public int Compare(System.Object x, System.Object y)
        {
            string strX = ((IniKey)x).Section;
            string strY = ((IniKey)y).Section;
            return (new CaseInsensitiveComparer()).Compare(strX, strY);
        }
    }
    #endregion

    public static class Ini
    {
        #region Lock
        private readonly static ConcurrentDictionary<string, ReaderWriterLockSlim> lockDict =
            new ConcurrentDictionary<string, ReaderWriterLockSlim>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Must be called when Ini class is not used
        /// </summary>
        public static void ClearLockDict()
        {
            lockDict.Clear();
        }
        #endregion

        #region GetKey - Need Test
        /// <summary>
        /// Get key's value from ini file.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="section"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        public static string GetKey(string file, IniKey iniKey)
        {
            IniKey[] iniKeys = InternalGetKeys(file, new IniKey[] { iniKey });
            return iniKeys[0].Value;
        }
        /// <summary>
        /// Get key's value from ini file.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="section"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        public static string GetKey(string file, string section, string key)
        {
            IniKey[] iniKeys = InternalGetKeys(file, new IniKey[] { new IniKey(section, key) });
            return iniKeys[0].Value;
        }
        /// <summary>
        /// Get key's value from ini file.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="iniKeys"></param>
        /// <returns>
        /// Found values are stroed in returned IniKey.
        /// </returns>
        public static IniKey[] GetKeys(string file, IniKey[] iniKeys)
        {
            return InternalGetKeys(file, iniKeys);
        }
        /// <summary>
        /// Get key's value from ini file.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="iniKeys"></param>
        /// <returns>
        /// Found values are stroed in returned IniKey.
        /// </returns>
        public static IniKey[] GetKeys(string file, IEnumerable<IniKey> iniKeys)
        {
            return InternalGetKeys(file, iniKeys.ToArray());
        }
        private static IniKey[] InternalGetKeys(string file, IniKey[] iniKeys)
        {
            ReaderWriterLockSlim rwLock;
            if (lockDict.ContainsKey(file))
            {
                rwLock = lockDict[file];
            }
            else
            {
                rwLock = new ReaderWriterLockSlim();
                lockDict[file] = rwLock;
            }
                
            rwLock.EnterReadLock();
            try
            {
                List<int> processedKeyIdxs = new List<int>();

                Encoding encoding = FileHelper.DetectTextEncoding(file);
                using (StreamReader reader = new StreamReader(file, encoding, true))
                {
                    // int len = iniKeys.Count;
                    string line = string.Empty;
                    bool inTargetSection = false;
                    string currentSection = null;

                    while ((line = reader.ReadLine()) != null)
                    { // Read text line by line
                        if (processedKeyIdxs.Count == iniKeys.Length) // Work Done
                            break;

                        line = line.Trim(); // Remove whitespace
                        if (line.StartsWith("#", StringComparison.Ordinal) ||
                            line.StartsWith(";", StringComparison.Ordinal) ||
                            line.StartsWith("//", StringComparison.Ordinal)) // Ignore comment
                            continue;

                        if (inTargetSection)
                        {
                            int idx = line.IndexOf('=');
                            if (idx != -1 && idx != 0) // there is key, and key name is not empty
                            {
                                string keyName = line.Substring(0, idx);
                                for (int i = 0; i < iniKeys.Length; i++)
                                {
                                    if (processedKeyIdxs.Contains(i))
                                        continue;

                                    // Only if <section, key> is same, copy value;
                                    IniKey iniKey = iniKeys[i];
                                    if (currentSection.Equals(iniKey.Section, StringComparison.OrdinalIgnoreCase) &&
                                        keyName.Equals(iniKey.Key, StringComparison.OrdinalIgnoreCase))
                                    {
                                        iniKey.Value = line.Substring(idx + 1);
                                        iniKeys[i] = iniKey;
                                        processedKeyIdxs.Add(i);
                                    }
                                }
                            }
                            else
                            {
                                // search if current section has end
                                if (line.StartsWith("[", StringComparison.Ordinal) &&
                                    line.EndsWith("]", StringComparison.Ordinal))
                                {
                                    // Only sections contained in iniKeys will be targeted
                                    inTargetSection = false;
                                    currentSection = null;
                                    string foundSection = line.Substring(1, line.Length - 2);
                                    for (int i = 0; i < iniKeys.Length; i++)
                                    {
                                        if (processedKeyIdxs.Contains(i))
                                            continue;

                                        if (foundSection.Equals(iniKeys[i].Section, StringComparison.OrdinalIgnoreCase))
                                        {
                                            inTargetSection = true;
                                            currentSection = foundSection;
                                            break; // for shorter O(n)
                                        }
                                    }
                                }
                            }
                        }
                        else
                        { // not in section
                          // Check if encountered section head Ex) [Process]
                            if (line.StartsWith("[", StringComparison.Ordinal) &&
                                line.EndsWith("]", StringComparison.Ordinal))
                            {
                                // Only sections contained in iniKeys will be targeted
                                string foundSection = line.Substring(1, line.Length - 2);
                                for (int i = 0; i < iniKeys.Length; i++)
                                {
                                    if (processedKeyIdxs.Contains(i))
                                        continue;

                                    if (foundSection.Equals(iniKeys[i].Section, StringComparison.OrdinalIgnoreCase))
                                    {
                                        inTargetSection = true;
                                        currentSection = foundSection;
                                        break; // for shorter O(n)
                                    }
                                }
                            }
                        }
                    }
                    reader.Close();
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }

            return iniKeys;
        }
        #endregion

        #region SetKey - Need Test
        /// <summary>
        /// Add key into ini file. Return true if success.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="section"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns>
        /// Found values are stroed in returned IniKey.
        /// </returns>
        public static bool SetKey(string file, string section, string key, string value)
        {
            return InternalSetKeys(file, new List<IniKey> { new IniKey(section, key, value) });
        }
        /// <summary>
        /// Add key into ini file. Return true if success.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="section"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns>
        /// Found values are stroed in returned IniKey.
        /// </returns>
        public static bool SetKey(string file, IniKey iniKey)
        {
            return InternalSetKeys(file, new List<IniKey> { iniKey });
        }
        /// <summary>
        /// Add key into ini file.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="section"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns>
        /// Found values are stroed in returned IniKey.
        /// </returns>
        public static bool SetKeys(string file, IniKey[] iniKeys)
        {
            return InternalSetKeys(file, iniKeys.ToList());
        }
        public static bool SetKeys(string file, List<IniKey> iniKeys)
        {
            return InternalSetKeys(file, iniKeys);
        }
        private static bool InternalSetKeys(string file, List<IniKey> iniKeys) 
        {
            ReaderWriterLockSlim rwLock;
            if (lockDict.ContainsKey(file))
            {
                rwLock = lockDict[file];
            }
            else
            {
                rwLock = new ReaderWriterLockSlim();
                lockDict[file] = rwLock;
            }

            rwLock.EnterWriteLock();
            try
            {
                bool fileExist = File.Exists(file);

                // If file do not exists or blank, just create new file and insert keys.
                if (fileExist == false)
                {
                    using (StreamWriter writer = new StreamWriter(file, false, Encoding.UTF8))
                    {
                        string beforeSection = string.Empty;
                        for (int i = 0; i < iniKeys.Count; i++)
                        {
                            if (beforeSection.Equals(iniKeys[i].Section, StringComparison.OrdinalIgnoreCase) == false)
                            {
                                if (0 < i)
                                    writer.WriteLine();
                                writer.WriteLine($"[{iniKeys[i].Section}]");
                            }
                            writer.WriteLine($"{iniKeys[i].Key}={iniKeys[i].Value}");
                            beforeSection = iniKeys[i].Section;
                        }
                        writer.Close();
                    }
                    return true;
                }

                List<int> processedKeys = new List<int>();
                string tempPath = FileHelper.CreateTempFile();
                Encoding encoding = FileHelper.DetectTextEncoding(file);
                using (StreamReader reader = new StreamReader(file, encoding, true))
                using (StreamWriter writer = new StreamWriter(tempPath, false, encoding))
                {
                    string rawLine = string.Empty;
                    string line = string.Empty;
                    bool inTargetSection = false;
                    string currentSection = null;
                    List<string> processedSections = new List<string>();

                    while ((rawLine = reader.ReadLine()) != null)
                    { // Read text line by line
                        bool thisLineWritten = false;
                        line = rawLine.Trim(); // Remove whitespace

                        // Ignore comments. If you wrote all keys successfully, also skip.
                        if (iniKeys.Count == 0 ||
                            line.StartsWith("#", StringComparison.Ordinal) ||
                            line.StartsWith(";", StringComparison.Ordinal) ||
                            line.StartsWith("//", StringComparison.Ordinal))
                        {
                            thisLineWritten = true;
                            writer.WriteLine(rawLine);
                            continue;
                        }

                        // Check if encountered section head Ex) [Process]
                        if (line.StartsWith("[", StringComparison.Ordinal) &&
                            line.EndsWith("]", StringComparison.Ordinal))
                        {
                            string foundSection = line.Substring(1, line.Length - 2);

                            if (inTargetSection)
                            { // End and start of the section
                                for (int i = 0; i < iniKeys.Count; i++)
                                {
                                    if (processedKeys.Contains(i))
                                        continue;

                                    if (currentSection.Equals(iniKeys[i].Section, StringComparison.OrdinalIgnoreCase))
                                    {
                                        processedKeys.Add(i);
                                        writer.WriteLine($"{iniKeys[i].Key}={iniKeys[i].Value}");
                                    }
                                }
                            }

                            // Start of the section
                            inTargetSection = false;
                            // Only sections contained in iniKeys will be targeted
                            for (int i = 0; i < iniKeys.Count; i++)
                            {
                                if (processedKeys.Contains(i))
                                    continue;

                                if (foundSection.Equals(iniKeys[i].Section, StringComparison.OrdinalIgnoreCase))
                                {
                                    inTargetSection = true;
                                    currentSection = foundSection;
                                    processedSections.Add(currentSection);
                                    break; // for shorter O(n)
                                }
                            }
                            thisLineWritten = true;
                            writer.WriteLine(rawLine);
                        }

                        // key=value
                        int idx = line.IndexOf('=');
                        if (idx != -1 && idx != 0)
                        {
                            if (inTargetSection) // process here only if we are in target section
                            {
                                string keyOfLine = line.Substring(0, idx);
                                for (int i = 0; i < iniKeys.Count; i++)
                                {
                                    if (processedKeys.Contains(i))
                                        continue;

                                    if (currentSection.Equals(iniKeys[i].Section, StringComparison.OrdinalIgnoreCase)
                                        && keyOfLine.Equals(iniKeys[i].Key, StringComparison.OrdinalIgnoreCase))
                                    { // key exists, so overwrite
                                        processedKeys.Add(i);
                                        thisLineWritten = true;
                                        writer.WriteLine($"{keyOfLine}={iniKeys[i].Value}");
                                    }
                                }

                                if (!thisLineWritten)
                                {
                                    thisLineWritten = true;
                                    writer.WriteLine(rawLine);
                                }
                            }
                            else
                            {
                                thisLineWritten = true;
                                writer.WriteLine(rawLine);
                            }
                        }

                        // Blank line
                        if (line.Equals(string.Empty, StringComparison.Ordinal))
                        {
                            if (inTargetSection)
                            {
                                for (int i = 0; i < iniKeys.Count; i++)
                                {
                                    if (processedKeys.Contains(i))
                                        continue;

                                    if (currentSection.Equals(iniKeys[i].Section, StringComparison.OrdinalIgnoreCase))
                                    { // append key to section
                                        processedKeys.Add(i);
                                        thisLineWritten = true;
                                        writer.WriteLine($"{iniKeys[i].Key}={iniKeys[i].Value}");
                                    }
                                }
                            }
                            thisLineWritten = true;
                            writer.WriteLine();
                        }

                        // End of file
                        if (reader.Peek() == -1)
                        {
                            if (inTargetSection)
                            { // Currently in section? check currentSection
                                for (int i = 0; i < iniKeys.Count; i++)
                                {
                                    if (processedKeys.Contains(i))
                                        continue;

                                    if (currentSection.Equals(iniKeys[i].Section, StringComparison.OrdinalIgnoreCase))
                                    {
                                        processedKeys.Add(i);
                                        if (thisLineWritten == false)
                                            writer.WriteLine(rawLine);
                                        thisLineWritten = true;
                                        writer.WriteLine($"{iniKeys[i].Key}={iniKeys[i].Value}");
                                    }
                                }
                            }

                            // Not in section, so create new section
                            for (int i = 0; i < iniKeys.Count; i++)
                            { // At this time, only unfound section remains in iniKeys
                                if (processedKeys.Contains(i))
                                    continue;

                                if (processedSections.Any(s => s.Equals(iniKeys[i].Section, StringComparison.OrdinalIgnoreCase)) == false)
                                {
                                    processedSections.Add(iniKeys[i].Section);
                                    writer.WriteLine($"\r\n[{iniKeys[i].Section}]");
                                }
                                processedKeys.Add(i);
                                writer.WriteLine($"{iniKeys[i].Key}={iniKeys[i].Value}");
                            }
                        }

                        if (!thisLineWritten)
                            writer.WriteLine(rawLine);
                    }
                    reader.Close();
                    writer.Close();
                }

                if (processedKeys.Count == iniKeys.Count)
                {
                    FileHelper.FileReplaceEx(tempPath, file);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            finally
            {
                rwLock.ExitWriteLock();
            }            
        }
        #endregion

        #region DeleteKey - need test
        public static bool DeleteKey(string file, IniKey iniKey)
        {
            return InternalDeleteKeys(file, new List<IniKey> { iniKey });
        }
        public static bool DeleteKey(string file, string section, string key)
        {
            return InternalDeleteKeys(file, new List<IniKey> { new IniKey(section, key) });
        }
        public static bool DeleteKeys(string file, IniKey[] iniKeys)
        {
            return InternalDeleteKeys(file, iniKeys.ToList());
        }
        public static bool DeleteKeys(string file, List<IniKey> iniKeys)
        {
            return InternalDeleteKeys(file, iniKeys);
        }
        private static bool InternalDeleteKeys(string file, List<IniKey> iniKeys)
        {
            ReaderWriterLockSlim rwLock;
            if (lockDict.ContainsKey(file))
            {
                rwLock = lockDict[file];
            }
            else
            {
                rwLock = new ReaderWriterLockSlim();
                lockDict[file] = rwLock;
            }

            rwLock.EnterWriteLock();
            try
            {
                if (File.Exists(file) == false)
                    return false;

                string tempPath = FileHelper.CreateTempFile();
                Encoding encoding = FileHelper.DetectTextEncoding(file);
                using (StreamReader reader = new StreamReader(file, encoding, true))
                using (StreamWriter writer = new StreamWriter(tempPath, false, encoding))
                {
                    if (reader.Peek() == -1)
                    {
                        reader.Close();
                        return false;
                    }

                    string rawLine = string.Empty;
                    string line = string.Empty;
                    bool inTargetSection = false;
                    string currentSection = null;

                    while ((rawLine = reader.ReadLine()) != null)
                    { // Read text line by linev
                        bool thisLineProcessed = false;
                        line = rawLine.Trim(); // Remove whitespace

                        // Ignore comments. If you deleted all keys successfully, also skip.
                        if (iniKeys.Count == 0
                            || line.StartsWith("#", StringComparison.Ordinal)
                            || line.StartsWith(";", StringComparison.Ordinal)
                            || line.StartsWith("//", StringComparison.Ordinal))
                        {
                            thisLineProcessed = true;
                            writer.WriteLine(rawLine);
                            continue;
                        }

                        // Check if encountered section head Ex) [Process]
                        if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                        {
                            string foundSection = line.Substring(1, line.Length - 2);

                            // Start of the section
                            inTargetSection = false;
                            // Only sections contained in iniKeys will be targeted
                            for (int i = 0; i < iniKeys.Count; i++)
                            {
                                if (foundSection.Equals(iniKeys[i].Section, StringComparison.OrdinalIgnoreCase))
                                {
                                    inTargetSection = true;
                                    currentSection = foundSection;
                                    break; // for shorter O(n)
                                }
                            }
                            thisLineProcessed = true;
                            writer.WriteLine(rawLine);
                        }

                        // key=value
                        int idx = line.IndexOf('=');
                        if (idx != -1 && idx != 0) // Key exists
                        {
                            if (inTargetSection) // process here only if we are in target section
                            {
                                string keyOfLine = line.Substring(0, idx);
                                for (int i = 0; i < iniKeys.Count; i++)
                                {
                                    if (currentSection.Equals(iniKeys[i].Section, StringComparison.OrdinalIgnoreCase)
                                        && keyOfLine.Equals(iniKeys[i].Key, StringComparison.OrdinalIgnoreCase))
                                    { // key exists, so do not write this line, which lead to 'deletion'
                                        iniKeys.RemoveAt(i);
                                        thisLineProcessed = true;
                                    }
                                }
                            }
                        }

                        if (thisLineProcessed == false)
                            writer.WriteLine(rawLine);
                    }
                    reader.Close();
                    writer.Close();
                }

                if (iniKeys.Count == 0)
                {
                    FileHelper.FileReplaceEx(tempPath, file);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }
        #endregion

        #region AddSection - need test
        public static bool AddSection(string file, IniKey iniKey)
        {
            return InternalAddSection(file, new List<string> { iniKey.Section });
        }
        public static bool AddSection(string file, string section)
        {
            return InternalAddSection(file, new List<string> { section });
        }
        public static bool AddSections(string file, IniKey[] iniKeys)
        {
            List<string> sections = new List<string>();
            foreach (IniKey key in iniKeys)
                sections.Add(key.Section);
            return InternalDeleteSection(file, sections);
        }
        public static bool AddSections(string file, List<IniKey> iniKeys)
        {
            List<string> sections = new List<string>();
            foreach (IniKey key in iniKeys)
                sections.Add(key.Section);
            return InternalAddSection(file, sections);
        }
        public static bool AddSections(string file, string[] sections)
        {
            return InternalAddSection(file, sections.ToList());
        }
        public static bool AddSections(string file, List<string> sections)
        {
            return InternalAddSection(file, sections);
        }
        private static bool InternalAddSection(string file, List<string> sections)
        {
            ReaderWriterLockSlim rwLock;
            if (lockDict.ContainsKey(file))
            {
                rwLock = lockDict[file];
            }
            else
            {
                rwLock = new ReaderWriterLockSlim();
                lockDict[file] = rwLock;
            }

            rwLock.EnterWriteLock();
            try
            {
                if (File.Exists(file) == false)
                {
                    using (StreamWriter writer = new StreamWriter(file, false, Encoding.UTF8))
                    {
                        foreach (string section in sections)
                            writer.WriteLine($"\r\n[{section}]");
                        writer.Close();
                    }
                    return true;
                }

                string tempPath = FileHelper.CreateTempFile();
                Encoding encoding = FileHelper.DetectTextEncoding(file);
                using (StreamReader reader = new StreamReader(file, encoding, true))
                using (StreamWriter writer = new StreamWriter(tempPath, false, encoding))
                {
                    if (reader.Peek() == -1)
                    {
                        reader.Close();
                        return false;
                    }

                    string rawLine = string.Empty;
                    string line = string.Empty;
                    List<string> processedSections = new List<string>();

                    while ((rawLine = reader.ReadLine()) != null)
                    { // Read text line by line
                        bool thisLineProcessed = false;
                        line = rawLine.Trim(); // Remove whitespace

                        // Check if encountered section head Ex) [Process]
                        if (line.StartsWith("[", StringComparison.Ordinal) &&
                            line.EndsWith("]", StringComparison.Ordinal))
                        {
                            string foundSection = line.Substring(1, line.Length - 2);

                            // Start of the section;
                            // Only sections contained in iniKeys will be targeted
                            for (int i = 0; i < sections.Count; i++)
                            {
                                if (foundSection.Equals(sections[i], StringComparison.OrdinalIgnoreCase))
                                { // Delete this section!
                                    processedSections.Add(foundSection);
                                    sections.RemoveAt(i);
                                    break; // for shorter O(n)
                                }
                            }
                            thisLineProcessed = true;
                            writer.WriteLine(rawLine);
                        }

                        if (thisLineProcessed == false)
                            writer.WriteLine(rawLine);

                        // End of file
                        if (reader.Peek() == -1)
                        { // If there are sections not added, add it now
                            List<int> processedIdxs = new List<int>();
                            for (int i = 0; i < sections.Count; i++)
                            { // At this time, only unfound section remains in iniKeys
                                if (processedSections.Any(s => s.Equals(sections[i], StringComparison.OrdinalIgnoreCase)) == false)
                                {
                                    processedSections.Add(sections[i]);
                                    writer.WriteLine($"\r\n[{sections[i]}]");
                                }
                                processedIdxs.Add(i);
                            }
                            foreach (int i in processedIdxs.OrderByDescending(x => x))
                                sections.RemoveAt(i);
                        }
                    }
                    reader.Close();
                    writer.Close();
                }

                if (sections.Count == 0)
                {
                    FileHelper.FileReplaceEx(tempPath, file);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }
        #endregion

        #region DeleteSection - need test
        public static bool DeleteSection(string file, IniKey iniKey)
        {
            return InternalDeleteSection(file, new List<string> { iniKey.Section });
        }
        public static bool DeleteSection(string file, string section)
        {
            return InternalDeleteSection(file, new List<string> { section });
        }
        public static bool DeleteSections(string file, IniKey[] iniKeys)
        {
            List<string> sections = new List<string>();
            foreach (IniKey key in iniKeys)
                sections.Add(key.Section);
            return InternalDeleteSection(file, sections);
        }
        public static bool DeleteSections(string file, List<IniKey> iniKeys)
        {
            List<string> sections = new List<string>();
            foreach (IniKey key in iniKeys)
                sections.Add(key.Section);
            return InternalDeleteSection(file, sections);
        }
        public static bool DeleteSections(string file, string[] sections)
        {
            return InternalDeleteSection(file, sections.ToList());
        }
        public static bool DeleteSections(string file, List<string> sections)
        {
            return InternalDeleteSection(file, sections);
        }
        private static bool InternalDeleteSection(string file, List<string> sections)
        {
            ReaderWriterLockSlim rwLock;
            if (lockDict.ContainsKey(file))
            {
                rwLock = lockDict[file];
            }
            else
            {
                rwLock = new ReaderWriterLockSlim();
                lockDict[file] = rwLock;
            }

            rwLock.EnterWriteLock();
            try
            {
                if (File.Exists(file) == false)
                    return false;

                string tempPath = FileHelper.CreateTempFile();
                Encoding encoding = FileHelper.DetectTextEncoding(file);
                using (StreamReader reader = new StreamReader(file, encoding, true))
                using (StreamWriter writer = new StreamWriter(tempPath, false, encoding))
                {
                    if (reader.Peek() == -1)
                    {
                        reader.Close();
                        return false;
                    }

                    string rawLine = string.Empty;
                    string line = string.Empty;
                    bool ignoreCurrentSection = false;

                    while ((rawLine = reader.ReadLine()) != null)
                    { // Read text line by line
                        bool thisLineProcessed = false;
                        line = rawLine.Trim(); // Remove whitespace

                        // Check if encountered section head Ex) [Process]
                        if (line.StartsWith("[", StringComparison.Ordinal) &&
                            line.EndsWith("]", StringComparison.Ordinal))
                        {
                            string foundSection = line.Substring(1, line.Length - 2);
                            ignoreCurrentSection = false;

                            // Start of the section;
                            // Only sections contained in iniKeys will be targeted
                            for (int i = 0; i < sections.Count; i++)
                            {
                                if (foundSection.Equals(sections[i], StringComparison.OrdinalIgnoreCase))
                                { // Delete this section!
                                    ignoreCurrentSection = true;
                                    sections.RemoveAt(i);
                                    break; // for shorter O(n)
                                }
                            }
                            thisLineProcessed = true;
                            writer.WriteLine(rawLine);
                        }

                        if (thisLineProcessed == false && ignoreCurrentSection == false)
                            writer.WriteLine(rawLine);
                    }
                    reader.Close();
                    writer.Close();
                }

                if (sections.Count == 0)
                {
                    FileHelper.FileReplaceEx(tempPath, file);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }
        #endregion

        #region Utility
        /// <summary>
        /// Parse INI style strings into dictionary
        /// </summary>
        /// <param name="lines"></param>
        /// <returns></returns>
        public static StringDictionary ParseIniLinesIniStyle(IEnumerable<string> lines)
        {
            return InternalParseIniLinesRegex(@"^([^=]+)=(.*)$", lines);
        }
        /// <summary>
        /// Parse PEBakery-Variable style strings into dictionary
        /// </summary>
        /// There in format of %VarKey%=VarValue
        /// <param name="lines"></param>
        /// <returns></returns>
        public static StringDictionary ParseIniLinesVarStyle(IEnumerable<string> lines)
        {
            return InternalParseIniLinesRegex(@"^%([^=]+)%=(.*)$", lines);
        }
        /// <summary>
        /// Parse strings with regex.
        /// </summary>
        /// <param name="regex"></param>
        /// <param name="lines"></param>
        /// <returns></returns>
        private static StringDictionary InternalParseIniLinesRegex(string regex, IEnumerable<string> lines)
        {
            StringDictionary dict = new StringDictionary(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                Regex regexInstance = new Regex(regex, RegexOptions.Compiled);
                MatchCollection matches = regexInstance.Matches(line);

                // Make instances of sections
                for (int i = 0; i < matches.Count; i++)
                {
                    string key = matches[i].Groups[1].Value.Trim();
                    string value = matches[i].Groups[2].Value.Trim();
                    dict[key] = value;
                }
            }
            return dict;
        }


        /// <summary>
        /// Parse section to dictionary.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="section"></param>
        /// <returns></returns>
        public static StringDictionary ParseIniSectionToDict(string file, string section)
        {
            List<string> lines = ParseIniSection(file, section);
            return ParseIniLinesIniStyle(lines);
        }

        public static List<string> ParseIniSection(string file, string section)
        {
            List<string> lines = new List<string>();

            Encoding encoding = FileHelper.DetectTextEncoding(file);
            using (StreamReader reader = new StreamReader(file, encoding, true))
            {
                string line = string.Empty;
                bool appendState = false;
                int idx = 0;
                while ((line = reader.ReadLine()) != null)
                { // Read text line by line
                    line = line.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal) &&
                        line.EndsWith("]", StringComparison.Ordinal))
                    { // Start of section
                        if (appendState)
                            break;
                        
                        string foundSection = line.Substring(1, line.Length - 2);
                        if (section.Equals(foundSection, StringComparison.OrdinalIgnoreCase))
                            appendState = true;
                    }
                    else if ((idx = line.IndexOf('=')) != -1)
                    { // valid ini key
                        if (idx == 0) // key is empty
                            throw new InvalidIniFormatException($"[{line}] has invalid format");
                        if (appendState)
                            lines.Add(line);
                    }
                }

                if (appendState == false) // Section not found
                    throw new SectionNotFoundException($"Section [{section}] not found");

                reader.Close();
            }
            return lines;
        }

        /// <summary>
        /// Parse section to dictionary array.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="section"></param>
        /// <returns></returns>
        public static StringDictionary[] ParseSectionsToDicts(string file, string[] sections)
        {
            List<string>[] lines = ParseIniSections(file, sections);
            StringDictionary[] dicts = new StringDictionary[lines.Length];
            for (int i = 0; i < lines.Length; i++)
                 dicts[i] = ParseIniLinesIniStyle(lines[i]);
            return dicts;
        }
        /// <summary>
        /// Parse sections to string 2D array.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="section"></param>
        /// <returns></returns>
        public static List<string>[] ParseIniSections(string file, IEnumerable<string> enumberableSections)
        {
            string[] sections = enumberableSections.Distinct().ToArray(); // Remove duplicate

            List<string>[] lines = new List<string>[sections.Length];
            for (int i = 0; i < sections.Length; i++)
                lines[i] = new List<string>();

            Encoding encoding = FileHelper.DetectTextEncoding(file);
            using (StreamReader reader = new StreamReader(file, encoding, true))
            {
                string line = string.Empty;
                int currentSection = -1; // -1 == empty, 0, 1, ... == index value of sections array
                int idx = 0;
                List<int> processedSectionIdxs = new List<int>();

                while ((line = reader.ReadLine()) != null)
                { // Read text line by line
                    if (sections.Length < processedSectionIdxs.Count)
                        break;

                    line = line.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal) &&
                        line.EndsWith("]", StringComparison.Ordinal))
                    { // Start of section
                        bool isSectionFound = false;
                        string foundSection = line.Substring(1, line.Length - 2);
                        for (int i = 0; i < sections.Length; i++)
                        {
                            if (processedSectionIdxs.Contains(i))
                                continue;

                            if (foundSection.Equals(sections[i], StringComparison.Ordinal))
                            {
                                isSectionFound = true;
                                processedSectionIdxs.Add(i);
                                currentSection = i;
                                break;
                            }
                        }
                        if (!isSectionFound)
                            currentSection = -1;
                    }
                    else if ((idx = line.IndexOf('=')) != -1)
                    { // valid ini key
                        if (idx == 0) // current section is target, and key is empty
                            throw new InvalidIniFormatException($"[{line}] has invalid format");
                        if (currentSection != -1)
                            lines[currentSection].Add(line);
                    }
                }

                if (sections.Length != processedSectionIdxs.Count) // Section not found
                {
                    StringBuilder b = new StringBuilder("Section [");
                    for (int i = 0; i < sections.Length; i++)
                    {
                        if (processedSectionIdxs.Contains(i) == false)
                        {
                            b.Append(sections[i]);
                            if (i + 1 < sections.Length)
                                b.Append(", ");
                        }
                    }
                    b.Append("] not found");
                    throw new SectionNotFoundException(b.ToString());
                }

                reader.Close();
            }

            return lines;
        }

        /// <summary>
        /// Get name of sections from INI file
        /// </summary>
        /// <param name="file"></param>
        /// <returns></returns>
        public static List<string> GetSectionNames(string file)
        {
            List<string> sections = new List<string>();

            Encoding encoding = FileHelper.DetectTextEncoding(file);
            using (StreamReader reader = new StreamReader(file, encoding, true))
            {
                string line = string.Empty;
                while ((line = reader.ReadLine()) != null)
                { // Read text line by line
                    line = line.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal) &&
                        line.EndsWith("]", StringComparison.Ordinal)) // Count sections
                        sections.Add(line.Substring(1, line.Length - 2));
                }

                reader.Close();
            }

            return sections;
        }

        /// <summary>
        /// Check if INI file has specified section
        /// </summary>
        /// <param name="file"></param>
        /// <param name="section"></param>
        /// <returns></returns>
        public static bool CheckSectionExist(string file, string section)
        {
            bool result = false;

            Encoding encoding = FileHelper.DetectTextEncoding(file);
            using (StreamReader reader = new StreamReader(file, encoding, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                { // Read text line by line
                    line = line.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal) &&
                        line.EndsWith("]", StringComparison.Ordinal))
                    {
                        if (section.Equals(line.Substring(1, line.Length - 2), StringComparison.OrdinalIgnoreCase))
                        {
                            result = true;
                            break;
                        }
                    }
                }

                reader.Close();
            }
                
            return result;
        }


        /// <summary>
        /// Return true if failed
        /// </summary>
        /// <param name="rawLine"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static bool GetKeyValueFromLine(string rawLine, out string key, out string value)
        {
            int idx = rawLine.IndexOf('=');
            if (idx != -1) // there is key
            {
                key = rawLine.Substring(0, idx);
                value = rawLine.Substring(idx + 1);
                return false;
            }
            else // No Ini Format!
            {
                key = string.Empty;
                value = string.Empty;
                return true;
            }
        }

        /// <summary>
        /// Return true if failed
        /// </summary>
        /// <param name="rawLines"></param>
        /// <param name="keys"></param>
        /// <param name="values"></param>
        /// <returns></returns>
        public static bool GetKeyValueFromLines(List<string> rawLines, out List<string> keys, out List<string> values)
        {
            keys = new List<string>();
            values = new List<string>();
            for (int i = 0; i < rawLines.Count; i++)
            {
                if (GetKeyValueFromLine(rawLines[i], out string key, out string value))
                    return true;
                keys.Add(key);
                values.Add(value);
            }

            return false;
        }
        #endregion
    }
}