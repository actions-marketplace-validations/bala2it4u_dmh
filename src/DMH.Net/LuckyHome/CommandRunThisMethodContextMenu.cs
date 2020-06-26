﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Helpers;
using EnvDTE;
using EnvDTE80;
using LuckyHome.Common;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace LuckyHome
{

    internal sealed class CommandRunThisMethodContextMenu
    {
        /// <summary>
        /// Command handler
        /// </summary>
        public const int CommandId = 256;

        public static readonly Guid CommandSet = new Guid("d9e9de0c-4922-4251-82a5-3fbb5598a182");

        private readonly AsyncPackage package;

        private DTE dte;

        private Document activeDocument;

        private TextSelection selectedLine;

        private CodeFunction fun;

        private CodeClass clas;

        private Project project;

        private string solutionDir;

        private InterfaceMapperWithClassControl interfaceMapperWithClassControl;

        private IVsWindowFrame windowFrame;

        private SchemaInfoCommon schemaInfoCommon;

        public static CommandRunThisMethodContextMenu Instance
        {
            get;
            private set;
        }

        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider => package;

        private CommandRunThisMethodContextMenu(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = (package ?? throw new ArgumentNullException("package"));
            commandService = (commandService ?? throw new ArgumentNullException("commandService"));
            CommandID command = new CommandID(CommandSet, 256);
            MenuCommand command2 = new MenuCommand(MenuItemCallback, command);
            commandService.AddCommand(command2);
        }

        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService = (await package.GetServiceAsync(typeof(IMenuCommandService))) as OleMenuCommandService;
            Instance = new CommandRunThisMethodContextMenu(package, commandService);
        }

        private async void MenuItemCallback(object sender, EventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            dte = (DTE)(await ServiceProvider.GetServiceAsync(typeof(DTE)));
            activeDocument = dte.ActiveDocument;
            selectedLine = (TextSelection)dte.ActiveDocument.Selection;
            fun = (CodeFunction)selectedLine.ActivePoint.get_CodeElement(vsCMElement.vsCMElementFunction);
            clas = (CodeClass)selectedLine.ActivePoint.get_CodeElement(vsCMElement.vsCMElementClass);
            if (clas.Access != vsCMAccess.vsCMAccessPublic)
            {
                VsShellUtilities.ShowMessageBox(package, "we support only public classes", "Error", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            if (clas.FullName.Contains('<'))
            {
                VsShellUtilities.ShowMessageBox(package, "we are not supporting this type classes", "Error", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            if (fun.FullName.Contains('<'))
            {
                VsShellUtilities.ShowMessageBox(package, "we are not supporting this type functions", "Error", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            project = dte.ActiveDocument.ProjectItem.ContainingProject;
            string tempSolutionDir = Path.GetDirectoryName(dte.Solution.FullName) + "\\.LuckyHome";
            if (!Directory.Exists(tempSolutionDir))
            {
                Directory.CreateDirectory(tempSolutionDir);
            }
            if (schemaInfoCommon == null || tempSolutionDir != solutionDir)
            {
                solutionDir = tempSolutionDir;
                if (schemaInfoCommon == null)
                {
                    schemaInfoCommon = new SchemaInfoCommon();
                }
                ClassFinder.ClearCache();
                schemaInfoCommon.SolutionDir = solutionDir;
                if (File.Exists(schemaInfoCommon.GetFilePath()))
                {
                    schemaInfoCommon.SchemaInfo = Json.Decode<SchemaInfo>(schemaInfoCommon.GetFileData());
                }
                else
                {
                    schemaInfoCommon.SchemaInfo = new SchemaInfo();
                }
            }
            dte.Events.BuildEvents.OnBuildDone += BuildEvents_OnBuildDone;
            IVsUIShell shell = (IVsUIShell)(await ServiceProvider.GetServiceAsync(typeof(SVsUIShell)));
            Command cmd = dte.Commands.Item("Build.BuildSolution", 0);
            Guid guid = new Guid(cmd.Guid);
            Guid pguidCmdGroup = guid;
            int iD = cmd.ID;
            object pvaIn = null;
            shell.PostExecCommand(ref pguidCmdGroup, (uint)iD, 0u, ref pvaIn);
        }

        private void BuildEvents_OnBuildDone(vsBuildScope Scope, vsBuildAction Action)
        {
            ThreadHelper.ThrowIfNotOnUIThread("BuildEvents_OnBuildDone");
            dte.Events.BuildEvents.OnBuildDone -= BuildEvents_OnBuildDone;
            if (dte.Solution.SolutionBuild.LastBuildInfo == 1)
            {
                VsShellUtilities.ShowMessageBox(package, "Please fix build error and retry", "Error", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            dte.Debugger.Breakpoints.Add(fun.FullName, "", selectedLine.TopLine);
            CodeFunction codeFunction = null;
            SchemaInfo schemaInfo = new SchemaInfo();
            schemaInfo.MethodToRun = fun.Name;
            schemaInfo.MethodType = (int)fun.Access;
            schemaInfo.MethodParameters = fun.Parameters.OfType<CodeParameter2>().ToList().ConvertAll((CodeParameter2 x) => x.Type.AsFullName);
            string text = clas.Namespace.Name + ".";
            schemaInfo.NameSpaceAndClass = text + clas.FullName.Replace(text, "").Replace('.', '+');
            schemaInfo.AssambleName = dte.ActiveDocument.ProjectItem.ContainingProject.Name;
            List<string> list = new List<string>();
            foreach (string item in dte.Solution.SolutionBuild.StartupProjects as Array)
            {
                list.Add(item);
            }
            schemaInfo.StartAppProject = list.ToArray();
            codeFunction = getConstructor(clas);
            List<ClassInfo> depandancyClasses = new List<ClassInfo>();
            List<InputValue> inputValues = new List<InputValue>();
            if (codeFunction != null)
            {
                getParamType(codeFunction, depandancyClasses, inputValues);
            }
            getInterfaceMapper(schemaInfo, depandancyClasses, inputValues);
        }

        private void getInterfaceMapper(SchemaInfo schemaInfo, List<ClassInfo> depandancyClasses, List<InputValue> inputValues)
        {
            ClassInfo[] array = depandancyClasses.Where((ClassInfo x) => x.NameSpaceAndInterfaceName != null && x.NameSpaceAndMappedClassName == null).ToArray();
            if (array.Length != 0)
            {
                interfaceClassMapping(dte, array, delegate (CodeClass[] classInfos)
                {
                    foreach (CodeClass codeClass in classInfos)
                    {
                        if (codeClass != null)
                        {
                            CodeFunction constructor = getConstructor(codeClass);
                            if (constructor != null)
                            {
                                getParamType(constructor, depandancyClasses, inputValues);
                            }
                        }
                    }
                    getInterfaceMapper(schemaInfo, depandancyClasses, inputValues);
                });
                return;
            }
            getParamType(fun, depandancyClasses, inputValues);
            getMethodParamTypes(schemaInfo, depandancyClasses, inputValues);
        }

        private void getMethodParamTypes(SchemaInfo schemaInfo, List<ClassInfo> depandancyClasses, List<InputValue> inputValues)
        {
            ClassInfo[] array = depandancyClasses.Where((ClassInfo x) => x.NameSpaceAndInterfaceName != null && x.NameSpaceAndMappedClassName == null).ToArray();
            if (array.Length != 0)
            {
                interfaceClassMapping(dte, array, delegate (CodeClass[] classInfos)
                {
                    foreach (CodeClass codeClass in classInfos)
                    {
                        CodeFunction constructor = getConstructor(codeClass);
                        if (constructor != null)
                        {
                            getParamType(constructor, depandancyClasses, inputValues);
                        }
                    }
                    getMethodParamTypes(schemaInfo, depandancyClasses, inputValues);
                });
            }
            else
            {
                callMethod(schemaInfo, depandancyClasses, inputValues);
            }
        }

        private void callMethod(SchemaInfo schemaInfo, List<ClassInfo> depandancyClasses, List<InputValue> inputValues)
        {
            ThreadHelper.ThrowIfNotOnUIThread("callMethod");
            if (windowFrame != null && windowFrame.IsVisible() == 0)
            {
                interfaceMapperWithClassControl.UseDefault = false;
                ErrorHandler.ThrowOnFailure(windowFrame.Hide());
            }
            schemaInfo.DepandancyClasses = depandancyClasses;
            schemaInfo.InputValues = inputValues.ToArray();
            schemaInfoCommon.SetFileData(Json.Encode(schemaInfoCommon.SchemaInfo));
            string str = Path.Combine(Path.GetDirectoryName(project.FullName), project.ConfigurationManager.ActiveConfiguration.Properties.Item("OutputPath").Value.ToString());
            string text = str + ".LuckyHome\\";
            if (!Directory.Exists(text))
            {
                Directory.CreateDirectory(text);
            }
            string methodBasedUniqueName = schemaInfo.GetMethodBasedUniqueName();
            File.Copy("Run.Me.Now.exe", str + "Run.Me.Now.exe", overwrite: true);
            File.Copy("LuckyHome.Common.dll", str + "LuckyHome.Common.dll", overwrite: true);
            File.Copy("System.Web.Helpers.dll", str + "System.Web.Helpers.dll", overwrite: true);
            if (File.Exists(str + "schemainfo.json"))
            {
                File.Delete(str + "schemainfo.json");
            }
            if (File.Exists(str + "luckyhome.config"))
            {
                File.Copy(str + "luckyhome.config", str + "Run.Me.Now.dll.config");
            }
            else if (File.Exists(str + project.Name + ".dll.config"))
            {
                File.Copy(str + project.Name + ".dll.config", str + "Run.Me.Now.dll.config", overwrite: true);
            }
            string fileName = str + "Run.Me.Now.exe";
            System.Diagnostics.Process process = System.Diagnostics.Process.Start(fileName);
            Attach(dte, process.Id);
            File.WriteAllText(str + "schemainfo.json", Json.Encode(schemaInfo));
            File.WriteAllText(text + methodBasedUniqueName, Json.Encode(schemaInfo));
        }

        public static void Attach(DTE dte, int processid)
        {
            ThreadHelper.ThrowIfNotOnUIThread("Attach");
            try
            {
                foreach (EnvDTE.Process localProcess in dte.Debugger.LocalProcesses)
                {
                    if (localProcess.ProcessID == processid)
                    {
                        localProcess.Attach();
                        break;
                    }
                }
            }
            catch
            {
            }
        }

        private static CodeFunction getConstructor(CodeClass clas)
        {
            ThreadHelper.ThrowIfNotOnUIThread("getConstructor");
            CodeFunction result = null;
            foreach (CodeFunction item in clas.Members.OfType<CodeFunction>())
            {
                if (item?.Name == clas.Name)
                {
                    result = item;
                    break;
                }
            }
            return result;
        }

        private void getParamType(CodeFunction constructorFunction, List<ClassInfo> depandancyClasses, List<InputValue> inputValues)
        {
            ThreadHelper.ThrowIfNotOnUIThread("getParamType");
            foreach (CodeParameter2 item in constructorFunction.Parameters.OfType<CodeParameter2>().ToList())
            {
                try
                {
                    Type type = Type.GetType(item.Type.AsFullName, throwOnError: false, ignoreCase: false);
                    type = ((type == null) ? Type.GetType(item.Type.CodeType.FullName.TrimEnd('?')) : type);
                    if (type != null || item.Type.AsFullName == typeof(string).FullName)
                    {
                        inputValues.Add(new InputValue
                        {
                            InputType = (type?.FullName ?? typeof(string).FullName),
                            InputName = item.FullName,
                            DefaultValue = item.DefaultValue
                        });
                        continue;
                    }
                }
                catch
                {
                }
                if (item.Type.TypeKind != 0)
                {
                    CodeInterface codeInterface = item.Type.CodeType as CodeInterface;
                    CodeClass codeClass = item.Type.CodeType as CodeClass;
                    ClassInfo classInfo = new ClassInfo();
                    CodeElement codeElement = null;
                    if (codeInterface != null)
                    {
                        string text = classInfo.NameSpaceAndInterfaceName = codeInterface.FullName;
                        addtoInterfaceList(depandancyClasses, classInfo);
                    }
                    else
                    {
                        if (codeClass == null)
                        {
                            CodeEnum codeEnum = item.Type.CodeType as CodeEnum;
                            if (codeEnum != null)
                            {
                                inputValues.Add(new InputValue
                                {
                                    InputType = codeEnum.FullName,
                                    InputName = item.FullName,
                                    DefaultValue = item.DefaultValue
                                });
                            }
                        }
                        if (codeClass != null)
                        {
                            if (codeClass.FullName.StartsWith("System.Lazy<"))
                            {
                                string text3 = classInfo.NameSpaceAndInterfaceName = codeClass.FullName.Split('<', '>')[1];
                                addtoInterfaceList(depandancyClasses, classInfo);
                            }
                            else
                            {
                                CodeFunction constructor = getConstructor(codeClass);
                                if (constructor != null)
                                {
                                    getParamType(constructor, depandancyClasses, inputValues);
                                }
                                classInfo.NameSpaceAndMappedClassName = codeClass?.FullName;
                                classInfo.AssambleName = codeElement?.FullName;
                                if (classInfo.NameSpaceAndInterfaceName == null)
                                {
                                    classInfo.NameSpaceAndInterfaceName = classInfo.NameSpaceAndMappedClassName;
                                }
                                addtoInterfaceList(depandancyClasses, classInfo);
                            }
                        }
                    }
                }
            }
        }

        private static void addtoInterfaceList(List<ClassInfo> depandancyClasses, ClassInfo classInfo)
        {
            if (!depandancyClasses.Any((ClassInfo x) => x.NameSpaceAndInterfaceName == classInfo.NameSpaceAndInterfaceName))
            {
                depandancyClasses.Add(classInfo);
            }
        }

        private void interfaceClassMapping(DTE dte, ClassInfo[] classInfos, Action<CodeClass[]> callbackOption)
        {
            ThreadHelper.ThrowIfNotOnUIThread("interfaceClassMapping");
            ToolWindowPane toolWindowPane = package.FindToolWindow(typeof(InterfaceMapperWithClass), 0, create: true);
            if (toolWindowPane == null || toolWindowPane.Frame == null)
            {
                throw new NotSupportedException("Cannot create tool window");
            }
            interfaceMapperWithClassControl = (toolWindowPane.Content as InterfaceMapperWithClassControl);
            windowFrame = (IVsWindowFrame)toolWindowPane.Frame;
            toolWindowPane.Caption = "Inferface Class Mapper";
            ErrorHandler.ThrowOnFailure(windowFrame.Show());
            interfaceMapperWithClassControl.SetInterfaceMapperWithClassControlInput(new InterfaceMapperWithClassControlInput
            {
                ClassInfos = classInfos,
                ProjectNames = ClassFinder.FindProjects(dte.Solution),
                GetClasses = ((Project project) => ClassFinder.FindProjectClass(project)),
                CallbackOption = callbackOption,
                SchemaInfoCommon = schemaInfoCommon,
                Close = ()=>
                {
                    ThreadHelper.ThrowIfNotOnUIThread("interfaceClassMapping");
                    ErrorHandler.ThrowOnFailure(windowFrame.Hide());
                },
                ClearCache = ()=>
                {
                    ClassFinder.ClearCache();
                }
            });
        }
    }
}