﻿using System;
using System.Collections.Generic;
using System.IO;
using GitCommands;

namespace PatchApply
{
    public class PatchManager
    {
        private List<Patch> _patches = new List<Patch>();
        private readonly PatchProcessor patchProcessor = new PatchProcessor();

        public string PatchFileName { get; set; }

        public string DirToPatch { get; set; }

        public List<Patch> Patches
        {
            get { return _patches; }
            set { _patches = value; }
        }

        public static string GetSelectedLinesAsPatch(string text, int selectionPosition, int selectionLength, bool staged)
        {
            //When there is no patch, return nothing
            if (string.IsNullOrEmpty(text))
                return null;

            // Ported from the git-gui tcl code to C#
            // see lib/diff.tcl

            if (text.EndsWith("\n\\ No newline at end of file\n"))
                text = text.Remove(text.Length - "\n\\ No newline at end of file\n".Length);

            // Devide diff into header and patch
            int patch_pos = text.IndexOf("@@");
            string header = text.Substring(0, patch_pos);
            string diff = text.Substring(patch_pos);

            // Get selection position and length
            int first = selectionPosition - patch_pos;
            int last = first + selectionLength;

            //Make sure the header is not in the selection
            if (first < 0)
            {
                last += first;
                first = 0;
            }

            // Round selection to previous and next line breaks to select the whole lines
            int first_l = diff.LastIndexOf("\n", first, first) + 1;
            int last_l = diff.IndexOf("\n", last);
            if (last_l == -1)
                last_l = diff.Length - 1;

            // Are we looking at a diff from the working dir or the staging area
            char to_context = staged ? '+' : '-';

            // this will hold the entire patch at the end
            string wholepatch = "";

            // loop until $first_l has reached $last_l
            // ($first_l is modified inside the loop!)
            while (first_l < last_l)
            {
                // search from $first_l backwards for lines starting with @@
                int i_l = diff.LastIndexOf("\n@@", first_l, first_l);
                if (i_l == -1 && diff.Substring(0, 2) != "@@")
                {
                    // if there's not a @@ above, then the selected range
                    // must have come before the first @@
                    i_l = diff.IndexOf("\n@@", first_l, last_l - first_l);
                    if (i_l == -1)
                    {
                        // if the @@ is not even in the selected range then
                        // any further action is useless because there is no
                        // selected data that can be applied
                        return null;
                    }
                }
                i_l++;
                // $i_l is now at the beginning of the first @@ line in 
                // front of first_l

                // pick start line number from hunk header
                // example: hh = "@@ -604,58 +604,105 @@ foo bar"
                string hh = diff.Substring(i_l, diff.IndexOf("\n", i_l) - i_l);
                // example: hh = "@@ -604"
                hh = hh.Split(',')[0];
                // example: hlh = "604"
                string hln = hh.Split('-')[1];

                // There is a special situation to take care of. Consider this
                // hunk:
                //
                //    @@ -10,4 +10,4 @@
                //     context before
                //    -old 1
                //    -old 2
                //    +new 1
                //    +new 2
                //     context after
                //
                // We used to keep the context lines in the order they appear in
                // the hunk. But then it is not possible to correctly stage only
                // "-old 1" and "+new 1" - it would result in this staged text:
                //
                //    context before
                //    old 2
                //    new 1
                //    context after
                //
                // (By symmetry it is not possible to *un*stage "old 2" and "new
                // 2".)
                //
                // We resolve the problem by introducing an asymmetry, namely,
                // when a "+" line is *staged*, it is moved in front of the
                // context lines that are generated from the "-" lines that are
                // immediately before the "+" block. That is, we construct this
                // patch:
                //
                //    @@ -10,4 +10,5 @@
                //     context before
                //    +new 1
                //     old 1
                //     old 2
                //     context after
                //
                // But we do *not* treat "-" lines that are *un*staged in a
                // special way.
                //
                // With this asymmetry it is possible to stage the change "old
                // 1" -> "new 1" directly, and to stage the change "old 2" ->
                // "new 2" by first staging the entire hunk and then unstaging
                // the change "old 1" -> "new 1".
                //
                // Applying multiple lines adds complexity to the special
                // situation.  The pre_context must be moved after the entire
                // first block of consecutive staged "+" lines, so that
                // staging both additions gives the following patch:
                //
                //    @@ -10,4 +10,6 @@
                //     context before
                //    +new 1
                //    +new 2
                //     old 1
                //     old 2
                //     context after

                // This is non-empty if and only if we are _staging_ changes;
                // then it accumulates the consecutive "-" lines (after
                // converting them to context lines) in order to be moved after
                // "+" change lines.
                string pre_context = "";

                int n = 0;
                int m = 0;
                // move $i_l to the first line after the @@ line $i_l pointed at
                i_l = diff.IndexOf("\n", i_l) + 1;
                string patch = "";

                // while $i_l is not at the end of the file and not 
                // at the next @@ line
                while (i_l < diff.Length - 1 && diff.Substring(i_l, 2) != "@@")
                {
                    // set $next_l to the beginning of the next 
                    // line after $i_l
                    int next_l = diff.IndexOf("\n", i_l) + 1;
                    if (next_l == 0)
                    {
                        next_l = diff.Length;
                        m--;
                        n--;
                    }

                    // get character at $i_l 
                    char c1 = diff[i_l];

                    // if $i_l is in selected range and the line starts 
                    // with either - or + this is a line to stage/unstage
                    if (first_l <= i_l && i_l < last_l && (c1 == '-' || c1 == '+'))
                    {
                        // set $ln to the content of the line at $i_l
                        string ln = diff.Substring(i_l, next_l - i_l);
                        // if line starts with -
                        if (c1 == '-')
                        {
                            // increase n counter by one
                            n++;
                            // update $patch
                            patch += pre_context + ln;
                            // reset $pre_context
                            pre_context = "";

                            // if line starts with +
                        }
                        else
                        {
                            // increase m counter by one
                            m++;
                            // update $patch
                            patch += ln;
                        }

                        // if the line doesn't start with either - or +
                        // this is a context line 
                    }
                    else if (c1 != '-' && c1 != '+')
                    {
                        // set $ln to the content of the line at $i_l
                        string ln = diff.Substring(i_l, next_l - i_l);
                        // update $patch
                        patch += pre_context + ln;
                        // increase counters by one each
                        n++;
                        m++;
                        // reset $pre_context
                        pre_context = "";

                        // if the line starts with $to_context (see earlier)
                        // the sign at the beginning should be stripped
                    }
                    else if (c1 == to_context)
                    {
                        // turn change line into context line
                        string ln = diff.Substring(i_l + 1, next_l - i_l - 1);
                        if (c1 == '-')
                            pre_context += " " + ln;
                        else
                            patch += " " + ln;
                        // increase counters by one each
                        n++;
                        m++;

                        // if the line starts with the opposite sign of
                        // $to_context this line should be removed
                    }
                    else
                    {
                        // a change in the opposite direction of
                        // to_context which is outside the range of
                        // lines to apply.
                        patch += pre_context;
                        pre_context = "";
                    }
                    // set $i_l to the next line
                    i_l = next_l;
                }
                // finished current hunk (reached @@ or file/diff end)

                // update $patch (makes sure $pre_context gets appended)
                patch += pre_context;
                // update $wholepatch with the current hunk
                wholepatch += "@@ -" + hln + "," + n + " +" + hln + "," + m + " @@\n" + patch;

                // set $first_l to first line after the next @@ line
                first_l = diff.IndexOf("\n", i_l) + 1;
                if (first_l == 0)
                    break;
            }
            // we are almost done, $wholepatch should no contain all the 
            // (modified) hunks

            return header + wholepatch;
        }


        public string LoadFile(string fileName)
        {
            try
            {
                using (var streamReader = new StreamReader(DirToPatch + fileName, Settings.Encoding))
                {
                    string retval = "";
                    string line;
                    while ((line = streamReader.ReadLine()) != null)
                    {
                        retval += line + "\n";
                    }

                    if (retval.Length > 0 && retval[retval.Length - 1] == '\n')
                        retval = retval.Remove(retval.Length - 1, 1);

                    return retval;
                }
            }
            catch
            {
                return "";
            }
        }

        public void SavePatch()
        {
            foreach (Patch patch in _patches)
            {
                if (!patch.Apply)
                    continue;
                string path = DirToPatch + patch.FileNameA;
                if (patch.Type == Patch.PatchType.DeleteFile)
                {
                    File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(path.Substring(0, path.LastIndexOfAny("\\/".ToCharArray())));
                    using(TextWriter tw = new StreamWriter(DirToPatch + patch.FileNameA, false))
                        tw.Write(patch.FileTextB);
                }
            }
        }

        public string GetMD5Hash(string input)
        {
            IEnumerable<byte> bs = GetUTF8EncodedBytes(input);
            var s = new System.Text.StringBuilder();
            foreach (byte b in bs)
            {
                s.Append(b.ToString("x2").ToLower());
            }
            return s.ToString();
        }

        private static IEnumerable<byte> GetUTF8EncodedBytes(string input)
        {
            var x = new System.Security.Cryptography.MD5CryptoServiceProvider();
            byte[] bs = System.Text.Encoding.UTF8.GetBytes(input);
            bs = x.ComputeHash(bs);
            return bs;
        }


        public void ApplyPatch(Patch patch)
        {
            patch.FileTextB = "";
            patch.Rate = 100;

            if (patch.Type == Patch.PatchType.DeleteFile)
            {
                handleDeletePatchType(patch);
                return;
            }

            if (patch.Text == null)
                return;

            string[] patchLines = patch.Text.Split('\n');

            if (patch.Type == Patch.PatchType.NewFile)
            {
                handleNewFilePatchType(patch, patchLines);
                return;
            }

            if (patch.Type == Patch.PatchType.ChangeFile)
            {
                handleChangeFilePatchType(patch, patchLines);
                return;
            }
        }

        private void handleChangeFilePatchType(Patch patch, IEnumerable<string> patchLines)
        {
            var fileLines = new List<string>();
            foreach (string s in LoadFile(patch.FileNameA).Split('\n'))
            {
                fileLines.Add(s);
            }

            int lineNumber = 0;
            foreach (string line in patchLines)
            {
                //Parse first line
                //@@ -1,4 +1,4 @@
                if (line.StartsWith("@@") && line.LastIndexOf("@@") > 0)
                {
                    string pos = line.Substring(3, line.LastIndexOf("@@") - 3).Trim();
                    string[] addrem = pos.Split('+', '-');
                    string[] oldLines = addrem[1].Split(',');

                    lineNumber = Int32.Parse(oldLines[0]) - 1;
                    continue;
                }

                if (line.StartsWith(" "))
                {
                    //Do some extra checks
                    if (line.Length > 0)
                    {
                        if (fileLines.Count > lineNumber && fileLines[lineNumber].CompareTo(line.Substring(1)) != 0)
                            patch.Rate -= 20;
                    }
                    else
                    {
                        if (fileLines.Count > lineNumber && fileLines[lineNumber] != "")
                            patch.Rate -= 20;
                    }

                    lineNumber++;
                }
                if (line.StartsWith("-"))
                {
                    if (line.Length > 0)
                    {
                        if (fileLines.Count > lineNumber && fileLines[lineNumber].CompareTo(line.Substring(1)) != 0)
                            patch.Rate -= 20;
                    }
                    else
                    {
                        if (fileLines.Count > lineNumber && fileLines[lineNumber] != "")
                            patch.Rate -= 20;
                    }

                    patch.Bookmarks.Add(lineNumber);

                    if (fileLines.Count > lineNumber)
                        fileLines.RemoveAt(lineNumber);
                    else
                        patch.Rate -= 20;
                }
                if (line.StartsWith("+"))
                {
                    string insertLine = "";
                    if (line.Length > 1)
                        insertLine = line.Substring(1);

                    //Is the patch allready applied?
                    if (fileLines.Count > lineNumber && fileLines[lineNumber].CompareTo(insertLine) == 0)
                    {
                        patch.Rate -= 20;
                    }

                    fileLines.Insert(lineNumber, insertLine);
                    patch.Bookmarks.Add(lineNumber);

                    lineNumber++;
                }
            }
            foreach (string patchedLine in fileLines)
            {
                patch.FileTextB += patchedLine + "\n";
            }
            if (patch.FileTextB.Length > 0 && patch.FileTextB[patch.FileTextB.Length - 1] == '\n')
                patch.FileTextB = patch.FileTextB.Remove(patch.FileTextB.Length - 1, 1);

            if (patch.Rate != 100)
                patch.Apply = false;
        }

        private void handleNewFilePatchType(Patch patch, IEnumerable<string> patchLines)
        {
            foreach (string line in patchLines)
            {
                if (line.Length > 0 && line.StartsWith("+"))
                {
                    if (line.Length > 4 && line.StartsWith("+ï»¿"))
                        patch.AppendText(line.Substring(4));
                    else
                        if (line.Length > 1)
                            patch.FileTextB += line.Substring(1);

                    patch.FileTextB += "\n";
                }
            }
            if (patch.FileTextB.Length > 0 && patch.FileTextB[patch.FileTextB.Length - 1] == '\n')
                patch.FileTextB = patch.FileTextB.Remove(patch.FileTextB.Length - 1, 1);
            patch.Rate = 100;

            if (File.Exists(DirToPatch + patch.FileNameB))
            {
                patch.Rate -= 40;
                patch.Apply = false;
            }
        }

        private void handleDeletePatchType(Patch patch)
        {
            patch.FileTextB = "";
            patch.Rate = 100;

            if (!File.Exists(DirToPatch + patch.FileNameA))
            {
                patch.Rate -= 40;
                patch.Apply = false;
            }
        }

        public void LoadPatch(string text, bool applyPatch)
        {
            using (var stream = new StringReader(text))
            {
                LoadPatchStream(stream, applyPatch);
            }
        }

        public void LoadPatchFile(bool applyPatch)
        {
            using (var re = new StreamReader(PatchFileName, Settings.Encoding))
            {
                LoadPatchStream(re, applyPatch);
            }
        }

        public void LoadPatchStream(TextReader reader, bool applyPatch)
        {
            _patches = patchProcessor.CreatePatchesFromReader(reader);

            if (!applyPatch)
                return;

            foreach (Patch patchApply in _patches)
            {
                if (patchApply.Apply)
                    ApplyPatch(patchApply);
            }
        }

        /// <summary>
        /// Counts number of characters on all lines in file up to line number specified.
        /// Currently doesn't check if line > lines.Length.
        /// Probably not be including newline characters in the count.
        /// Not set up to handle DOS (CR LF) line endings.
        /// 
        /// Assumes file is a text file and that line < lines.Length/>
        /// </summary>
        /// <param name="file">file we want to contain lines from</param>
        /// <param name="line">line number we want to count up to</param>
        /// <returns></returns>
        public int LineToChar(string file, int line)
        {
            string[] lines = file.Split('\n');

            int retVal = 0;

            for (int n = 0; n < line; n++)
            {
                retVal += lines[n].Length;
            }

            return retVal;
        }
    }
}