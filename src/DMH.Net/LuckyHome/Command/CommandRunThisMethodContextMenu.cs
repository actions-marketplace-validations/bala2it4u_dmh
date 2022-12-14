using System;
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

namespace LuckyHome
{

    internal sealed partial class CommandRunThisMethodContextMenu
    {
        /// <summary>
        /// Command handler
        /// </summary>
        public const int CommandId = 256;

        public static readonly Guid CommandSet = new Guid("d9e9de0c-4922-4251-82a5-3fbb5598a182");
        ILogger logger =
                LogManager.GetLogger(typeof(CommandRunThisMethodContextMenu));

        private readonly AsyncPackage package;

        private DTE dte;

        private Document activeDocument;

        private TextSelection selectedLine;

        private CodeFunction fun;

        private CodeClass clas;

        private Project project;

        private string solutionDir;

        private InterfaceMapperWithClassControl interfaceMapperWithClassControl;
        private InputWindowControl inputWindowControl;
        private IVsWindowFrame windowInputFrame;
        private IVsWindowFrame windowFrame;

        private SchemaInfoCommon schemaInfoCommon;
        private List<Project> projects;

        #region * Common

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
            LogManager.GetLogger(typeof(CommandRunThisMethodContextMenu))
            .Info("InitializeAsync created");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService = (await package.GetServiceAsync(typeof(IMenuCommandService))) as OleMenuCommandService;
            Instance = new CommandRunThisMethodContextMenu(package, commandService);
        }

        #endregion

        private async void MenuItemCallback(object sender, EventArgs e)
        {
            logger.Info("MenuItemCallback clicked");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            dte = (DTE)(await ServiceProvider.GetServiceAsync(typeof(DTE)));
            activeDocument = dte.ActiveDocument;
            selectedLine = (TextSelection)dte.ActiveDocument.Selection;
            fun = (CodeFunction)selectedLine.ActivePoint.get_CodeElement(vsCMElement.vsCMElementFunction);
            clas = (CodeClass)selectedLine.ActivePoint.get_CodeElement(vsCMElement.vsCMElementClass);
            if (clas == null)
            {
                VsShellUtilities.ShowMessageBox(package, "please select valid class and try, if valid please build project and try again", "Error", 
                    OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            if (fun == null)
            {
                VsShellUtilities.ShowMessageBox(package, "please select valid method and try", "Error", 
                    OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
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
            string tempSolutionDir = string.Empty;
            try
            {
                tempSolutionDir = Path.GetDirectoryName(dte.Solution.FullName) + "\\.LuckyHome";
            }
            catch
            {
                VsShellUtilities.ShowMessageBox(package,
                    "please save the Solution and then try running!", "Error", 
                    OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            project = dte.ActiveDocument.ProjectItem.ContainingProject;
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
            logger.Info("MenuItemCallback ended");
        }

        private void BuildEvents_OnBuildDone(vsBuildScope Scope, vsBuildAction Action)
        {
            ThreadHelper.ThrowIfNotOnUIThread("BuildEvents_OnBuildDone");
            logger.Info("BuildEvents_OnBuildDone called");
            dte.Events.BuildEvents.OnBuildDone -= BuildEvents_OnBuildDone;
            if (dte.Solution.SolutionBuild.LastBuildInfo == 1)
            {
                VsShellUtilities.ShowMessageBox(package, "Please fix build error and retry", "Error", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                logger.Error("BuildEvents_OnBuildDone build failed");
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

            string tempPath = Path.GetTempPath() + ".LuckyHome\\" + Path.GetFileNameWithoutExtension(project.FullName);
            string methodBasedUniqueName = schemaInfo.GetMethodBasedUniqueName();
            schemaInfo.FullMethodBasedUniqueName = tempPath + methodBasedUniqueName;
            if (!Directory.Exists(tempPath))
            {
                Directory.CreateDirectory(tempPath);
            }
            //var tempProjects = dte.Solution.Projects.Cast<Project>();
            projects = ClassFinder.FindProjects(dte.Solution);
            List<string> list = new List<string>();
            foreach (string item in dte.Solution.SolutionBuild.StartupProjects as Array)
            {
                // sorry, you'll have to OfType<Project>() on Projects (dte is my wrapper)
                // find my Project from the build context based on its name.  Vomit.
                var project = projects.First(x => x.UniqueName.EndsWith(item));
                // Combine the project's path (FullName == path???) with the 
                // OutputPath of the active configuration of that project
                var tempProperties = project.ConfigurationManager.ActiveConfiguration.Properties;
                var dir = Path.Combine(
                                    Path.GetDirectoryName(project.FullName),
                                    tempProperties.Item("OutputPath").Value.ToString(),
                                    project.Properties.Item("OutputFilename").Value.ToString()
                                    );
                // and combine it with the OutputFilename to get the assembly
                // or skip this and grab all files in the output directory
                list.Add(dir);
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
            logger.Info("BuildEvents_OnBuildDone ended");
        }

        private void getInterfaceMapper(SchemaInfo schemaInfo, List<ClassInfo> depandancyClasses, List<InputValue> inputValues)
        {
            logger.Info("getInterfaceMapper called");
            ClassInfo[] array = depandancyClasses.Where((ClassInfo x) => x.NameSpaceAndInterfaceName != null && x.NameSpaceAndMappedClassName == null).ToArray();
            if (array.Length != 0)
            {
                interfaceClassMapping(schemaInfo, array, (CodeClass[] classInfos) =>
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
            logger.Info("getMethodParamTypes called");
            ClassInfo[] array = depandancyClasses.Where((ClassInfo x) => x.NameSpaceAndInterfaceName != null && x.NameSpaceAndMappedClassName == null).ToArray();
            if (array.Length != 0)
            {
                interfaceClassMapping(schemaInfo, array, (CodeClass[] classInfos) =>
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
            logger.Info("callMethod called");

            schemaInfo.DepandancyClasses = depandancyClasses;
            schemaInfo.InputValues = inputValues.ToArray();
            schemaInfoCommon.SetFileData(Json.Encode(schemaInfoCommon.SchemaInfo));

            ThreadHelper.ThrowIfNotOnUIThread("callMethod");
            logger.Info("fileCopyAndDebug called");
            if (windowFrame != null && windowFrame.IsVisible() == 0)
            {
                interfaceMapperWithClassControl.UseDefault = false;
                ErrorHandler.ThrowOnFailure(windowFrame.Hide());
            }
            inputWindowShow(schemaInfo, () =>
                {
                    if (windowInputFrame != null && windowInputFrame.IsVisible() == 0)
                    {
                        //interfaceMapperWithClassControl.UseDefault = false;
                        ErrorHandler.ThrowOnFailure(windowInputFrame.Hide());
                    }
                    fileCopyAndDebug(schemaInfo);
                });
        }

        private void fileCopyAndDebug(SchemaInfo schemaInfo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var tempTargetFramework = project.Properties.Item("TargetFrameworkMoniker").Value + "";

            if (tempTargetFramework.StartsWith(".NETCoreApp,Version=v2."))
            {
                new FileCopyAndStartDebugCore2Frame().Run(schemaInfo, solutionDir, dte);
            }
            else if (tempTargetFramework.StartsWith(".NETCoreApp,Version=v3."))
            {
                new FileCopyAndStartDebugCore3Frame().Run(schemaInfo, solutionDir, dte);
            }
            else
            {
                _ = new FileCopyAndStartDebugFrameWork().Run(schemaInfo, solutionDir, dte);
            }
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
            catch(Exception ex)
            {
                LogManager.GetLogger(typeof(CommandRunThisMethodContextMenu))
                .Error("attach failed", ex);
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
            logger.Info("getParamType called");
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
                            InputName = item.FullName,//$"{constructorFunction.FullName}->{item.FullName}",
                            DefaultValue = item.DefaultValue?.Trim('"')
                        });
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error("getParamType exception", ex);
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

        private void interfaceClassMapping(SchemaInfo schemaInfo, ClassInfo[] classInfos, Action<CodeClass[]> callbackOption)
        {
            ThreadHelper.ThrowIfNotOnUIThread("interfaceClassMapping");
            logger.Info("interfaceClassMapping called");
            ToolWindowPane toolWindowPane = package.FindToolWindow(typeof(InterfaceMapperWithClass), 0, create: true);
            if (toolWindowPane == null || toolWindowPane.Frame == null)
            {
                throw new NotSupportedException("Cannot create tool window");
            }
            interfaceMapperWithClassControl = (toolWindowPane.Content as InterfaceMapperWithClassControl);
            windowFrame = (IVsWindowFrame)toolWindowPane.Frame;
            toolWindowPane.Caption = "Interface Class Mapper";
            ErrorHandler.ThrowOnFailure(windowFrame.Show());
            interfaceMapperWithClassControl.SetInterfaceMapperWithClassControlInput(new InterfaceMapperWithClassControlInput
            {
                ClassInfos = classInfos,
                ProjectNames = ClassFinder.FindProjects(dte.Solution),
                GetClasses = ((Project project) => ClassFinder.FindProjectClass(project)),
                GetRefClasses = ((Project project) => ClassFinder.FindProjectReferance(project)),
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
                },
                LastRunFound = File.Exists(schemaInfo.FullMethodBasedUniqueName),
                CallbackLastRunOption = ()=>
                {
                    var tempSchemaInfo = Json.Decode<SchemaInfo>(File.ReadAllText(schemaInfo.FullMethodBasedUniqueName));
                    fileCopyAndDebug(tempSchemaInfo);
                }
            });
        }

        private void inputWindowShow(SchemaInfo schemaInfo, Action callbackOption)
        {
            ThreadHelper.ThrowIfNotOnUIThread("InputWindow");
            logger.Info("InputWindow called");
            ToolWindowPane toolWindowPane = package.FindToolWindow(typeof(InputWindow), 0, create: true);
            if (toolWindowPane == null || toolWindowPane.Frame == null)
            {
                logger.Error("Cannot create tool Input window");
                throw new NotSupportedException("Cannot create tool Input window");
            }
            inputWindowControl = (toolWindowPane.Content as InputWindowControl);
            windowInputFrame = (IVsWindowFrame)toolWindowPane.Frame;
            toolWindowPane.Caption = "Input Class/Method";
            ErrorHandler.ThrowOnFailure(windowInputFrame.Show());
            inputWindowControl.SetInterfaceMapperWithClassControlInput(new InputWindowControlInput
            {
                ProjectNames = ClassFinder.FindProjects(dte.Solution),
                CallbackOption = callbackOption,

                SchemaInfoCommon = schemaInfoCommon,
                SchemaInfo = schemaInfo,
                Close = () =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread("interfaceClassMapping");
                    ErrorHandler.ThrowOnFailure(windowInputFrame.Hide());
                },
                LastRunFound = File.Exists(schemaInfo.FullMethodBasedUniqueName) ? Json.Decode<SchemaInfo>(File.ReadAllText(schemaInfo.FullMethodBasedUniqueName)) : null,
                
            });
        }
    }
}
