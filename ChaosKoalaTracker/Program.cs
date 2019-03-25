using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ChaosKoalaTracker
{
    class Program
    {
        const int CHECK_INTERVAL = 10;
        const int SECONDS = 1000;
        static bool RunApp = true;
        static Thread FolderWatcherThread;
        static Dictionary<string, FileStateInfo> MasterDictionaryOfFiles = new Dictionary<string, FileStateInfo>(); //Using locking for thread safety instead of usng ConcurrentDictionary
        static readonly object objectLocker = new object();

        /// All the work in this region is aimed at capturing a shutdown call
        #region Trap application termination
        enum CtrlType
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }

        [DllImport("Kernel32")]
        private static extern bool SetConsoleCtrlHandler(EventHandler handler, bool add);

        private delegate bool EventHandler(CtrlType sig);
        static EventHandler handler;

        private static bool Handler(CtrlType sig)
        {
            Notify("Call to shut down received. Attempting to close cleanly.");

            RunApp = false;
            PreShutDown();

            Environment.Exit(-1);
            return true;
        }
        #endregion

        static void Main(string[] args)
        {
            //*The program takes 2 arguments:
            //  the directory to watch and a file pattern 
            //  example: ChaosKoalaTracker.exe "c:\file folder" *.txt
            if (!ValidateArgs(args))
            {
                PrintHint();
                return;
            }

            string fileDirectory = args[0];
            string searchPattern = args[1];

            //the action the main scanning thread will use
            Action folderMonitorThreadWrapper = () => 
            {
                //decouple thread.sleep() from execution (a little)
                DateTime nextCheck = DateTime.Now.AddSeconds(CHECK_INTERVAL);
                while (RunApp)
                {
                    if (nextCheck <= DateTime.Now)
                    {
                        Parallel.Invoke(() =>
                        {
                            ScanAndProcessFolder(fileDirectory, searchPattern);
                            nextCheck = DateTime.Now.AddSeconds(CHECK_INTERVAL);
                        });
                    }
                    Thread.Sleep(SECONDS/10);
                }
            };

            // wire up handle close window event
            handler += new EventHandler(Handler);
            SetConsoleCtrlHandler(handler, true);

            //get initial state
            PerformInitialScan(fileDirectory, searchPattern);

            ////run the main thread
            RunApp = true;
            FolderWatcherThread = new Thread(new ThreadStart(folderMonitorThreadWrapper));
            FolderWatcherThread.Name = "Chaos Koala Hunter Main Thread";
            FolderWatcherThread.IsBackground = true;
            FolderWatcherThread.Start();

            //do nothing with the main program thread
            while (RunApp)
            {
                Thread.Sleep(60 * SECONDS);
            }
            //shutdown will be handled from SetConsoleCtrlHandler above
        }

        /// <summary>
        /// Preforms shutdown cleanup and messaging.
        /// </summary>
        private static void PreShutDown()
        {
            RunApp = false;
            if(null != FolderWatcherThread && FolderWatcherThread.IsAlive)
                FolderWatcherThread.Abort();
            Console.WriteLine("Chaos Koala hunt terminated.");
        }

        /// <summary>
        /// Gets an updated state of the folder being scans and
        /// </summary>
        /// <param name="FileDirectory">The directory to scan.</param>
        /// <param name="SearchPattern">File search filter.</param>
        private static void ScanAndProcessFolder(string FileDirectory, string SearchPattern)
        {
            Dictionary<string, FileStateInfo> currentState = GetFolderState(FileDirectory, SearchPattern);
            ProcessFolderChanges(currentState);
        }

        /// <summary>
        /// Updates the master file record and reports the changes.
        /// </summary>
        /// <param name="currentState">A Dictionary (presumably more recent than the master file record).</param>
        private static void ProcessFolderChanges(Dictionary<string, FileStateInfo> currentState)
        {
            //Custom Except function to take advantage of FileStateInfo.ContentEquals()
            Func<Dictionary<string, FileStateInfo>, Dictionary<string, FileStateInfo>, IEnumerable<KeyValuePair<string, FileStateInfo>>> Except = (dictA, dictB) =>
            {
                return dictA.Where(kvp => 
                {
                    return !dictB.ContainsKey(kvp.Key) 
                    || !kvp.Value.ContentEquals(dictB[kvp.Key]);
                });
            };

            var diffNotInNewData = Except(MasterDictionaryOfFiles, currentState).ToList();
            var diffNotInOldData = Except(currentState, MasterDictionaryOfFiles).ToList();
            var receivedUpdate = diffNotInOldData.Where(od => diffNotInNewData.Any(nd => nd.Key == od.Key)).ToList();

            Parallel.ForEach(diffNotInNewData, (kvp) =>
            {
                if (!receivedUpdate.Any(u => u.Key == kvp.Key))
                    RemoveFile(kvp.Key, kvp.Value);
            });

            Parallel.ForEach(diffNotInOldData, (kvp) =>
            {
                if (!receivedUpdate.Any(u => u.Key == kvp.Key))
                    AddFile(kvp.Key, kvp.Value);
                else
                    UpdateFile(kvp.Key, kvp.Value);
            });
        }

        /// <summary>
        /// All-purpose function for writing to the console.
        /// </summary>
        /// <param name="Message">The message to send to output.</param>
        private static void Notify(string Message)
        {
            Console.WriteLine(string.Format(Message));
        }

        /// <summary>
        /// Adds a file to the master record of files.
        /// </summary>
        /// <param name="FileName">The file.</param>
        /// <param name="StateInfo">The FileStateInfo object describing the file.</param>
        private static void AddFile(string FileName, FileStateInfo StateInfo)
        {
            lock (objectLocker)
            {
                MasterDictionaryOfFiles.Add(Path.GetFileName(FileName).ToUpper(), StateInfo);
            }
            Notify($"{"Added:",-8} \"{Path.GetFileName(StateInfo.FileName)}\"");
        }

        /// <summary>
        /// Updates a file in the master record of files.
        /// </summary>
        /// <param name="FileName">The file.</param>
        /// <param name="StateInfo">The FileStateInfo object describing the file.</param>
        private static void UpdateFile(string FileName, FileStateInfo StateInfo)
        {
            long lineDiff = 0;
            lock (objectLocker)
            {
                FileStateInfo previousStateInfo = MasterDictionaryOfFiles[FileName];
                lineDiff = StateInfo.LineCount - previousStateInfo.LineCount;
                MasterDictionaryOfFiles[Path.GetFileName(FileName).ToUpper()] = StateInfo;
            }
            string alteration = (lineDiff == 0) ? "Same amount of lines in" : (lineDiff > 0) ? $"{lineDiff} lines added to" : $"{Math.Abs(lineDiff)} lines removed from";
            Notify($"{"Altered:",-8} \"{Path.GetFileName(StateInfo.FileName)}\" :: {alteration} new file.");
        }

        /// <summary>
        /// Removes a file from the master record of files.
        /// </summary>
        /// <param name="FileName">The file.</param>
        /// <param name="StateInfo">The FileStateInfo object describing the file.</param>
        private static void RemoveFile(string FileName, FileStateInfo StateInfo)
        {
            lock (objectLocker)
            {
                MasterDictionaryOfFiles.Remove(FileName.ToUpper());
            }
            Notify($"{"Removed:",-8} \"{Path.GetFileName(StateInfo.FileName)}\"");
        }

        /// <summary>
        /// Resets the state of internal master record of files.
        /// </summary>
        /// <param name="FileDirectory">The directory to scan.</param>
        /// <param name="SearchPattern">File search filter.</param>
        private static void PerformInitialScan(string FileDirectory, string SearchPattern)
        {
            Notify($"Scanning \"{FileDirectory}\" for pattern \"{SearchPattern}\"");
            lock (objectLocker)
            {
                MasterDictionaryOfFiles = GetFolderState(FileDirectory, SearchPattern);
            }
            foreach(var kvp in MasterDictionaryOfFiles)
            {
                Notify($"Found File: \"{Path.GetFileName(kvp.Value.FileName)}\"");
            }
        }

        /// <summary>
        /// Very basic check of string array presumably entered as command-line params.
        /// </summary>
        /// <param name="args">Expected: 2 strings, the first describing a folder path, the second describing a search filter.</param>
        /// <returns>Bool stating whether the argumenst are valid.</returns>
        private static bool ValidateArgs(string[] args)
        {
            //no requirement to create the folder if it doesnt exist
            return (args.Length == 2) && (Directory.Exists(Path.GetFullPath(args[0])));
        }

        /// <summary>
        /// Scans a folder and returns a Dictionary of information about the files contained therein.
        /// </summary>
        /// <param name="FileDirectory">The directory to scan.</param>
        /// <param name="SearchPattern">File search filter.</param>
        /// <returns>Dictionary of files and file information found in the scan.</returns>
        private static Dictionary<string, FileStateInfo> GetFolderState(string FileDirectory, string SearchPattern)
        {
            Action< Dictionary<string, FileStateInfo>,string> processFile = (dict, fileName) => {
                FileStateInfo stateInfo = GetFileStateInfo(fileName);
                //Requirement: File names are case insensitive. So Im casting the key to uppercase. 
                //Storing the case-sensitive name in the FileStateInfo for display (less jarring to the reader).
                dict.Add(Path.GetFileName(fileName).ToUpper(), stateInfo);
            };

            Dictionary<string, FileStateInfo> result = new Dictionary<string, FileStateInfo>();
            string[] fileEntries = Directory.GetFiles(Path.GetFullPath(FileDirectory), SearchPattern);
            Parallel.ForEach(fileEntries, (fileName) =>
            {
                //try-catch just for file locking issues. requirements dont specifically state what to do with a lock
                //so until new requirements we'll skip locked files and catch them next scan.
                try
                {
                    processFile(result,fileName);
                }
                catch(Exception ex)
                {
                    Func<Exception,bool> isFileLockException = (excp) => 
                    {
                        if (ex is AggregateException)
                        {
                            foreach (Exception except in ((AggregateException)ex).InnerExceptions)
                            {
                                if (except is IOException && except.Message.Contains("because it is being used by another process"))
                                    return true;
                            }
                        }
                        else if (ex is IOException && ex.Message.Contains("because it is being used by another process"))
                            return true;

                        return false;
                    };

                    if (!isFileLockException(ex))
                        throw ex;
                }
            });
            return result;
        }

        /// <summary>
        /// Gets an object describing a file.
        /// </summary>
        /// <param name="FileName">The file.</param>
        /// <returns>An object containing file name, line count, and last modifired date.</returns>
        private static FileStateInfo GetFileStateInfo(string FileName)
        {
            DateTime lastModified = File.GetLastWriteTimeUtc(FileName);
            long lineCount = CountLinesInFile(FileName);
            return new FileStateInfo(FileName, lastModified, lineCount);
        }

        /// <summary>
        /// All purpose response when the users enters unuable parameters.
        /// </summary>
        private static void PrintHint()
        {
            Console.WriteLine("This program takes 2 arguments, the directory to watch and a file pattern,\n\tExample: ChaosKoalaTracker.exe \"c:\\file folder\\\" *.txt");
            Console.WriteLine("\tThe first argument is a path to any existing folder. You may use an absolute, relative, or UNC path");
            Console.WriteLine("\tThe second argument is the file filter to apply to the folder scan.");
            Console.WriteLine("");
        }

        /// <summary>
        /// Counts the number of lines in a text file.
        /// Lovingly stolen from http://www.nimaara.com/2018/03/20/counting-lines-of-a-text-file/
        /// </summary>
        /// <param name="FileName">The file to assess</param>
        /// <returns>The number of lines in the file.</returns>
        public static long CountLinesInFile(string FileName)
        {
            //this is the fastest method, and smallest resource use but is much more complex code 
            //to manage and only slightly faster than counting by ReadLine() 
            char CR = '\r';
            char LF = '\n';
            char NULL = (char)0;
            var stream = new MemoryStream(File.ReadAllBytes(FileName));
            long lineCount = 0L;
            var byteBuffer = new byte[1024 * 1024];
            const int BytesAtTheTime = 4;
            var detectedEOL = NULL;
            var currentChar = NULL;

            int bytesRead;
            while ((bytesRead = stream.Read(byteBuffer, 0, byteBuffer.Length)) > 0)
            {
                var i = 0;
                for (; i <= bytesRead - BytesAtTheTime; i += BytesAtTheTime)
                {
                    currentChar = (char)byteBuffer[i];

                    if (detectedEOL != NULL)
                    {
                        if (currentChar == detectedEOL) { lineCount++; }

                        currentChar = (char)byteBuffer[i + 1];
                        if (currentChar == detectedEOL) { lineCount++; }

                        currentChar = (char)byteBuffer[i + 2];
                        if (currentChar == detectedEOL) { lineCount++; }

                        currentChar = (char)byteBuffer[i + 3];
                        if (currentChar == detectedEOL) { lineCount++; }
                    }
                    else
                    {
                        if (currentChar == LF || currentChar == CR)
                        {
                            detectedEOL = currentChar;
                            lineCount++;
                        }
                        i -= BytesAtTheTime - 1;
                    }
                }

                for (; i < bytesRead; i++)
                {
                    currentChar = (char)byteBuffer[i];

                    if (detectedEOL != NULL)
                    {
                        if (currentChar == detectedEOL) { lineCount++; }
                    }
                    else
                    {
                        if (currentChar == LF || currentChar == CR)
                        {
                            detectedEOL = currentChar;
                            lineCount++;
                        }
                    }
                }
            }

            if (currentChar != LF && currentChar != CR && currentChar != NULL)
            {
                lineCount++;
            }
            return lineCount;
        }
    }

    class FileStateInfo
    {
        /// <summary>
        /// Stores the case-sensitive spelling of the file name
        /// </summary>
        public string FileName;

        /// <summary>
        /// Date and time the file was last modified. Stored as UTC.
        /// </summary>
        public DateTime LastModifiedDate;

        /// <summary>
        /// Count of lines in the file.
        /// </summary>
        public long LineCount = 0;

        public FileStateInfo(string fileName, DateTime lastModifiedDate, long lineCount)
        {
            FileName = fileName;
            LastModifiedDate = lastModifiedDate;
            LineCount = lineCount;
        }

        /// <summary>
        /// Checks if a StateInfo object contains identical values
        /// </summary>
        /// <param name="StateInfo"></param>
        /// <returns></returns>
        public bool ContentEquals(FileStateInfo StateInfo)
        {            
            return (FileName.ToUpper() == StateInfo.FileName.ToUpper() && 
                LastModifiedDate == StateInfo.LastModifiedDate &&
                LineCount == StateInfo.LineCount);
        }
    }
}
