using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

/// <summary>
/// MSBuild logger that emits a compile_commands.json from a C++ project build.
/// 
/// Behavior is unchanged from the previous versions:
/// - Same parameter parsing and defaults.
/// - Same environment include handling (INCLUDE and EXTERNAL_INCLUDE appended as /I).
/// - Same task filtering and command-line parsing.
/// - Same file classification logic and output structure.
/// - Same memory free semantics for CommandLineToArgvW.
/// - Also adds /I for directories containing PCH headers named by /Yc or /Yu (split or combined forms).
/// </summary>
public class CompileCommandsJson : Logger
{
    public override void Initialize(IEventSource eventSource)
    {
        outputFilePath = "compile_commands.json";
        const bool append = false;

        string compileCommandsPath = Environment.GetEnvironmentVariable("COMPILE_COMMANDS_PATH");
        if (compileCommandsPath != null && compileCommandsPath.Length > 0)
        {
            outputFilePath = compileCommandsPath;
        }
        string compileCommandsLog = Environment.GetEnvironmentVariable("COMPILE_COMMANDS_LOG_PATH");
        if (compileCommandsLog != null && compileCommandsLog.Length > 0)
        {
            logFilePath = compileCommandsLog;
        }
        if (!string.IsNullOrEmpty(Parameters))
        {
            string[] args = Parameters.Split(',');
            for (int i = 0; i < args.Length; ++i)
            {
                string arg = args[i];
                if (arg.ToLower().StartsWith("path="))
                {
                    outputFilePath = arg.Substring(5);
                }
                else if (arg.ToLower().StartsWith("task="))
                {
                    customTask = arg.Substring(5);
                }
                else if (arg.ToLower().StartsWith("log="))
                {
                    string logFile = arg.Substring(4);
                    if (!string.IsNullOrEmpty(logFile))
                    {
                        logFilePath = logFile;
                    }
                    else
                    {
                        logFilePath = "compile_commands.log";
                    }
                }
                else
                {
                    throw new LoggerException($"Unknown argument in compile command logger: {arg}");
                }
            }
        }

        if (!string.IsNullOrEmpty(logFilePath)) {
            if (logFilePath.ToLower().StartsWith("stdout")) {
                logStreamWriter = new StreamWriter(Console.OpenStandardOutput());
                logStreamWriter.AutoFlush = true;
                Console.SetOut(logStreamWriter);
            } else {
                Console.WriteLine("Using " + logFilePath + " for logging");
                logStreamWriter = new StreamWriter(logFilePath, append, new UTF8Encoding(false));
            }
        }

        string logStdout = Environment.GetEnvironmentVariable("MSBUILD_LOG_STDOUT");
        if (logStdout != null && logStdout.ToLower() == "true")
        {
            logStreamWriter = new StreamWriter(Console.OpenStandardOutput());
            logStreamWriter.AutoFlush = true;
            Console.SetOut(logStreamWriter);
        }

        includeLookup = new Dictionary<string, bool>();
        eventSource.AnyEventRaised += EventSource_AnyEventRaised;

        try
        {
            commandLookup = new Dictionary<string, CompileCommand>();
            if (File.Exists(outputFilePath))
            {
                compileCommands = JsonConvert.DeserializeObject<List<CompileCommand>>(File.ReadAllText(outputFilePath));
            }

            if (compileCommands == null)
            {
                compileCommands = new List<CompileCommand>();
            }

            foreach (CompileCommand command in compileCommands)
            {
                string key = command.directory + command.file;
                commandLookup.Add(key, command);
            }
        }
        catch (Exception ex)
        {
            if (ex is UnauthorizedAccessException
                || ex is ArgumentNullException
                || ex is PathTooLongException
                || ex is DirectoryNotFoundException
                || ex is NotSupportedException
                || ex is ArgumentException
                || ex is SecurityException
                || ex is IOException)
            {
                throw new LoggerException($"Failed to create {outputFilePath}: {ex.Message}");
            }
            else
            {
                throw;
            }
        }
    }

    private void EventSource_AnyEventRaised(object sender, BuildEventArgs args)
    {
        AppendEnvIncludes("INCLUDE");
        AppendEnvIncludes("EXTERNAL_INCLUDE");

        if (args is TaskCommandLineEventArgs taskArgs)
        {
            if (!(taskArgs.TaskName == "CL" || taskArgs.TaskName == "TrackedExec" || (!string.IsNullOrEmpty(customTask) && taskArgs.TaskName.Contains(customTask))))
            {
                return;
            }

            const string clExe = "cl.exe ";
            int clExeIndex = taskArgs.CommandLine.ToLower().IndexOf(clExe);
            if (clExeIndex == -1)
            {
                throw new LoggerException($"Unexpected lack of CL.exe in {taskArgs.CommandLine}");
            }

            List<string> arguments = new List<string>();

            string compilerPath = taskArgs.CommandLine.Substring(0, clExeIndex + clExe.Length - 1);
            arguments.Add(Path.GetFullPath(compilerPath));

            string argsString = taskArgs.CommandLine.Substring(clExeIndex + clExe.Length)
                .Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').TrimEnd();
            argsString = Regex.Replace(argsString, @"\s+", " ");
            string[] cmdArgs = CommandLineToArgs(argsString);

            string[] optionsWithParam = {
                "D", "I", "F", "U", "FI", "FU",
                "analyze:log", "analyze:stacksize", "analyze:max_paths",
                "analyze:ruleset", "analyze:plugin"};

            List<string> maybeFilenames = new List<string>();
            List<string> filenames = new List<string>();
            List<string> pchHeaders = new List<string>();
            bool allFilenamesAreSources = false;

            for (int i = 0; i < cmdArgs.Length; i++)
            {
                bool isOption = cmdArgs[i].StartsWith("/") || cmdArgs[i].StartsWith("-");
                string option = isOption ? cmdArgs[i].Substring(1) : "";
                bool isFile = false;

                if (isOption)
                {
                    if (option == "Yc" || option == "Yu")
                    {
                        if (i + 1 < cmdArgs.Length && !(cmdArgs[i + 1].StartsWith("/") || cmdArgs[i + 1].StartsWith("-")))
                        {
                            string hdrPeek = cmdArgs[i + 1];
                            if (!string.IsNullOrEmpty(hdrPeek)) pchHeaders.Add(hdrPeek.Trim('"'));
                        }
                    }
                    else if (option.StartsWith("Yc") || option.StartsWith("Yu"))
                    {
                        string hdr = option.Substring(2);
                        if (!string.IsNullOrEmpty(hdr)) pchHeaders.Add(hdr.Trim('"'));
                    }
                }

                if (isOption && Array.Exists(optionsWithParam, e => e == option))
                {
                    arguments.Add(cmdArgs[i++]);
                }
                else if (option == "Tc" || option == "Tp")
                {
                    if (i + 1 < cmdArgs.Length)
                    {
                        filenames.Add(cmdArgs[i + 1]);
                    }
                }
                else if (option.StartsWith("Tc") || option.StartsWith("Tp"))
                {
                    filenames.Add(option.Substring(2));
                }
                else if (option == "TC" || option == "TP")
                {
                    allFilenamesAreSources = true;
                }
                else if (option == "link")
                {
                    break;
                }
                else if (isOption || cmdArgs[i].StartsWith("@"))
                {
                    // ignore
                }
                else
                {
                    maybeFilenames.Add(cmdArgs[i]);
                    isFile = true;
                }

                if (!isFile)
                {
                    arguments.Add(cmdArgs[i]);
                }
            }

            if (includeLookup.Count > 0)
            {
                foreach (string path in includeLookup.Keys)
                {
                    arguments.Add("/I" + path);
                }
            }

            foreach (var hdr in pchHeaders)
            {
                if (hdr.IndexOf('\\') >= 0 || hdr.IndexOf('/') >= 0)
                {
                    string dir = Path.GetDirectoryName(hdr);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        arguments.Add("/I" + dir);
                    }
                }
            }

            log("*** Arguments " + string.Join(" ", arguments));
            log("*** MaybeFilenames " + string.Join(" ", maybeFilenames));

            foreach (string filename in maybeFilenames)
            {
                if (allFilenamesAreSources)
                {
                    filenames.Add(filename);
                }
                else
                {
                    int suffixPos = filename.LastIndexOf('.');
                    if (suffixPos != -1)
                    {
                        string ext = filename.Substring(suffixPos + 1).ToLowerInvariant();
                        if (ext == "c" || ext == "cxx" || ext == "cpp")
                        {
                            filenames.Add(filename);
                        }
                    }
                }
            }

            log("*** Filenames " + string.Join(" ", filenames));

            string dirname = Path.GetDirectoryName(taskArgs.ProjectFile);

            foreach (string filename in filenames)
            {
                CompileCommand command;
                string key = dirname + filename;
                List<string> prms = new List<string>(arguments);
                prms.Add(filename);

                if (commandLookup.ContainsKey(key))
                {
                    command = commandLookup[key];
                    command.file = filename;
                    command.directory = dirname;
                    command.arguments = prms;
                }
                else
                {
                    command = new CompileCommand() { file = filename, directory = dirname, arguments = prms };
                    compileCommands.Add(command);
                    commandLookup.Add(key, command);
                }
            }
        }
    }

    private void AppendEnvIncludes(string envVar)
    {
        string include = Environment.GetEnvironmentVariable(envVar);
        if (include == null) return;

        string[] includePaths = include.Split(';');
        foreach (string path in includePaths)
        {
            if (path.Length > 0 && !includeLookup.ContainsKey(path))
            {
                includeLookup.Add(path, true);
            }
        }
        log($"*** {envVar} " + include);
    }

    [DllImport("shell32.dll", SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

    static string[] CommandLineToArgs(string commandLine)
    {
        int argc;
        var argv = CommandLineToArgvW(commandLine, out argc);
        if (argv == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
        try
        {
            var args = new string[argc];
            for (var i = 0; i < args.Length; i++)
            {
                var p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                args[i] = Marshal.PtrToStringUni(p);
            }
            return args;
        }
        finally
        {
            Marshal.FreeHGlobal(argv);
        }
    }

    public override void Shutdown()
    {
        File.WriteAllText(outputFilePath, JsonConvert.SerializeObject(compileCommands, Formatting.Indented));
        if (logStreamWriter != null) {
            logStreamWriter.Close();
        }
        base.Shutdown();
    }

    void log(string message)
    {
        if (logStreamWriter != null) {
            logStreamWriter.WriteLine(message);
        }
    }

    class CompileCommand
    {
        public string directory;
        public List<string> arguments;
        public string file;
    }

    string customTask;
    string outputFilePath;
    string logFilePath;
    private List<CompileCommand> compileCommands;
    private Dictionary<string, CompileCommand> commandLookup;
    private Dictionary<string, bool> includeLookup;
    private StreamWriter logStreamWriter;
}
