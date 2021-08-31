﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;
using Volo.Abp.Cli.Commands;
using Volo.Abp.Cli.Http;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Http.Modeling;
using Volo.Abp.Json;
using Volo.Abp.Modularity;

namespace Volo.Abp.Cli.ServiceProxy.CSharp
{
    public class CSharpServiceProxyGenerator : ServiceProxyGeneratorBase<CSharpServiceProxyGenerator>, ITransientDependency
    {
        public const string Name = "CSHARP";

        private const string UsingPlaceholder = "<using placeholder>";
        private const string MethodPlaceholder = "<method placeholder>";
        private const string ClassName = "<className>";
        private const string ServiceInterface = "<serviceInterface>";
        private const string ServicePostfix = "AppService";
        private const string DefaultNamespace = "ClientProxies";
        private const string Namespace = "<namespace>";
        private const string AppServicePrefix = "Volo.Abp.Application.Services";
        private readonly string _clientProxyTemplate = "// This file is automatically generated by ABP framework to use MVC Controllers from CSharp" +
                                                       $"{Environment.NewLine}<using placeholder>" +
                                                       $"{Environment.NewLine}" +
                                                       $"{Environment.NewLine}namespace <namespace>" +
                                                       $"{Environment.NewLine}{{" +
                                                       $"{Environment.NewLine}    [Dependency(ReplaceServices = true)]" +
                                                       $"{Environment.NewLine}    [ExposeServices(typeof(<serviceInterface>))]" +
                                                       $"{Environment.NewLine}    public partial class <className> : ClientProxyBase<<serviceInterface>>, <serviceInterface>" +
                                                       $"{Environment.NewLine}    {{" +
                                                       $"{Environment.NewLine}        <method placeholder>" +
                                                       $"{Environment.NewLine}    }}" +
                                                       $"{Environment.NewLine}}}" +
                                                       $"{Environment.NewLine}";
        private readonly string _clientProxyPartialTemplate = "// This file is part of <className>, you can customize it here" +
                                                              $"{Environment.NewLine}namespace <namespace>" +
                                                              $"{Environment.NewLine}{{" +
                                                              $"{Environment.NewLine}    public partial class <className>" +
                                                              $"{Environment.NewLine}    {{" +
                                                              $"{Environment.NewLine}    }}" +
                                                              $"{Environment.NewLine}}}" +
                                                              $"{Environment.NewLine}";
        private readonly List<string> _usingNamespaceList = new()
        {
            "using System;",
            "using System.Threading.Tasks;",
            "using Volo.Abp.DependencyInjection;",
            "using Volo.Abp.Application.Dtos;",
            "using Volo.Abp.Http.Client;",
            "using Volo.Abp.Http.Client.ClientProxying;",
            "using Volo.Abp.Http.Modeling;"
        };

        public CSharpServiceProxyGenerator(
            CliHttpClientFactory cliHttpClientFactory,
            IJsonSerializer jsonSerializer) :
            base(cliHttpClientFactory, jsonSerializer)
        {
        }

        public override async Task GenerateProxyAsync(GenerateProxyArgs args)
        {
            CheckFolder(args.Folder);
            var projectFilePath = CheckWorkDirectory(args.WorkDirectory);

            if (args.CommandName == RemoveProxyCommand.Name)
            {
                RemoveClientProxyFile(args);
                return;
            }

            var rootNamespace = GetRootNamespace(projectFilePath);

            var applicationApiDescriptionModel = await GetApplicationApiDescriptionModelAsync(args);

            foreach (var controller in applicationApiDescriptionModel.Modules.Values.SelectMany(x => x.Controllers))
            {
                if (ShouldGenerateProxy(controller.Value))
                {
                    await GenerateClientProxyFileAsync(args, controller.Value, rootNamespace);
                }
            }

            await CreateGenerateProxyJsonFile(args, applicationApiDescriptionModel);
        }

        private async Task CreateGenerateProxyJsonFile(GenerateProxyArgs args, ApplicationApiDescriptionModel applicationApiDescriptionModel)
        {
            var folder = args.Folder.IsNullOrWhiteSpace()? DefaultNamespace : args.Folder;
            var filePath = Path.Combine(args.WorkDirectory, folder, $"{args.Module}-generate-proxy.json");

            using (var writer = new StreamWriter(filePath))
            {
                await writer.WriteAsync(JsonSerializer.Serialize(applicationApiDescriptionModel, indented: true));
            }
        }

        private void RemoveClientProxyFile(GenerateProxyArgs args)
        {
            var folder = args.Folder.IsNullOrWhiteSpace()? DefaultNamespace : args.Folder;
            var folderPath = Path.Combine(args.WorkDirectory, folder);

            if (Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, true);
            }

            Logger.LogInformation($"Delete {GetLoggerOutputPath(folderPath, args.WorkDirectory)}");
        }

        private async Task GenerateClientProxyFileAsync(
            GenerateProxyArgs args,
            ControllerApiDescriptionModel controllerApiDescription,
            string rootNamespace)
        {
            var appServiceTypeFullName = controllerApiDescription.Interfaces.Last().Type;
            var appServiceTypeName = appServiceTypeFullName.Split('.').Last();

            var folder = args.Folder.IsNullOrWhiteSpace()? DefaultNamespace : args.Folder;
            var usingNamespaceList = new List<string>(_usingNamespaceList);

            var clientProxyName = $"{controllerApiDescription.ControllerName}ClientProxy";
            var clientProxyBuilder = new StringBuilder(_clientProxyTemplate);
            var fileNamespace = $"{rootNamespace}.{folder.Replace('/', '.')}";
            usingNamespaceList.Add($"using {GetTypeNamespace(appServiceTypeFullName)};");

            clientProxyBuilder.Replace(ClassName, clientProxyName);
            clientProxyBuilder.Replace(Namespace, fileNamespace);
            clientProxyBuilder.Replace(ServiceInterface, appServiceTypeName);

            foreach (var action in controllerApiDescription.Actions.Values)
            {
                if (!ShouldGenerateMethod(appServiceTypeFullName, action))
                {
                    continue;
                }

                GenerateMethod(action, clientProxyBuilder, usingNamespaceList);
            }

            foreach (var usingNamespace in usingNamespaceList)
            {
                clientProxyBuilder.Replace($"{UsingPlaceholder}", $"{usingNamespace}{Environment.NewLine}{UsingPlaceholder}");
            }

            clientProxyBuilder.Replace($"{Environment.NewLine}{UsingPlaceholder}", string.Empty);
            clientProxyBuilder.Replace($"{Environment.NewLine}        {MethodPlaceholder}", string.Empty);

            var filePath = Path.Combine(args.WorkDirectory, folder, clientProxyName + ".cs");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            using (var writer = new StreamWriter(filePath))
            {
                await writer.WriteAsync(clientProxyBuilder.ToString());
                Logger.LogInformation($"Create {GetLoggerOutputPath(filePath, args.WorkDirectory)}");
            }

            await GenerateClientProxyPartialFileAsync(args, clientProxyName, fileNamespace, filePath);
        }

        private async Task GenerateClientProxyPartialFileAsync(
            GenerateProxyArgs args,
            string clientProxyName,
            string fileNamespace,
            string filePath)
        {
            var clientProxyBuilder = new StringBuilder(_clientProxyPartialTemplate);
            clientProxyBuilder.Replace(ClassName, clientProxyName);
            clientProxyBuilder.Replace(Namespace, fileNamespace);

            filePath = filePath.Replace(".cs", ".partial.cs");

            if (!File.Exists(filePath))
            {
                using (var writer = new StreamWriter(filePath))
                {
                    await writer.WriteAsync(clientProxyBuilder.ToString());
                }

                Logger.LogInformation($"Create {GetLoggerOutputPath(filePath, args.WorkDirectory)}");
            }
        }

        private void GenerateMethod(
            ActionApiDescriptionModel action,
            StringBuilder clientProxyBuilder,
            List<string> usingNamespaceList)
        {
            var methodBuilder = new StringBuilder();

            var returnTypeName = GetRealTypeName(usingNamespaceList, action.ReturnValue.Type);

            if(!action.Name.EndsWith("Async"))
            {
                GenerateSynchronizationMethod(action, returnTypeName, methodBuilder, usingNamespaceList);
                clientProxyBuilder.Replace(MethodPlaceholder, $"{methodBuilder} {Environment.NewLine}        {MethodPlaceholder}");
                return;
            }

            GenerateAsynchronousMethod(action, returnTypeName, methodBuilder, usingNamespaceList);
            clientProxyBuilder.Replace(MethodPlaceholder, $"{methodBuilder} {Environment.NewLine}        {MethodPlaceholder}");
        }

        private void GenerateSynchronizationMethod(ActionApiDescriptionModel action, string returnTypeName, StringBuilder methodBuilder, List<string> usingNamespaceList)
        {
            methodBuilder.AppendLine($"public virtual {returnTypeName} {action.Name}(<args>)");

            foreach (var parameter in action.Parameters.GroupBy(x => x.Name).Select( x=> x.First()))
            {
                methodBuilder.Replace("<args>", $"{GetRealTypeName(usingNamespaceList, parameter.Type)} {parameter.Name}, <args>");
            }

            methodBuilder.Replace("<args>", string.Empty);
            methodBuilder.Replace(", )", ")");

            methodBuilder.AppendLine("        {");
            methodBuilder.AppendLine("            //Client Proxy does not support the synchronization method, you should always use asynchronous methods as a best practice");
            methodBuilder.AppendLine("            throw new System.NotImplementedException(); ");
            methodBuilder.AppendLine("        }");
        }

        private void GenerateAsynchronousMethod(
            ActionApiDescriptionModel action,
            string returnTypeName,
            StringBuilder methodBuilder,
            List<string> usingNamespaceList)
        {
            var returnSign = returnTypeName == "void" ? "Task": $"Task<{returnTypeName}>";

            methodBuilder.AppendLine($"public virtual async {returnSign} {action.Name}(<args>)");

            foreach (var parameter in action.ParametersOnMethod)
            {
                methodBuilder.Replace("<args>", $"{GetRealTypeName(usingNamespaceList, parameter.Type)} {parameter.Name}, <args>");
            }

            methodBuilder.Replace("<args>", string.Empty);
            methodBuilder.Replace(", )", ")");

            methodBuilder.AppendLine("        {");

            if (returnTypeName == "void")
            {
                methodBuilder.AppendLine($"            await RequestAsync(nameof({action.Name}), <args>);");
            }
            else
            {
                methodBuilder.AppendLine($"            return await RequestAsync<{returnTypeName}>(nameof({action.Name}), <args>);");
            }

            foreach (var parameter in action.ParametersOnMethod)
            {
                methodBuilder.Replace("<args>", $"{parameter.Name}, <args>");
            }

            methodBuilder.Replace("<args>", string.Empty);
            methodBuilder.Replace(", )", ")");
            methodBuilder.AppendLine("        }");
        }

        private bool ShouldGenerateProxy(ControllerApiDescriptionModel controllerApiDescription)
        {
            if (!controllerApiDescription.Interfaces.Any())
            {
                return false;
            }

            var serviceInterface = controllerApiDescription.Interfaces.Last();
            return serviceInterface.Type.EndsWith(ServicePostfix);
        }

        private static bool ShouldGenerateMethod(string appServiceTypeName, ActionApiDescriptionModel action)
        {
            return action.ImplementFrom.StartsWith(AppServicePrefix) || action.ImplementFrom.StartsWith(appServiceTypeName);
        }

        private static string GetTypeNamespace(string typeFullName)
        {
            return typeFullName.Substring(0, typeFullName.LastIndexOf('.'));
        }

        private string GetRealTypeName(List<string> usingNamespaceList, string typeName)
        {
            var filter = new []{"<", ",", ">"};
            var stringBuilder = new StringBuilder();
            var typeNames = typeName.Split('.');

            if (typeNames.All(x => !filter.Any(x.Contains)))
            {
                AddUsingNamespace(usingNamespaceList, typeName);
                return NormalizeTypeName(typeNames.Last());
            }

            var fullName = string.Empty;

            foreach (var item in typeNames)
            {
                if (filter.Any(x => item.Contains(x)))
                {
                    AddUsingNamespace(usingNamespaceList, $"{fullName}.{item}".TrimStart('.'));
                    fullName = string.Empty;

                    if (item.Contains('<') || item.Contains(','))
                    {
                        stringBuilder.Append(item.Substring(0, item.IndexOf(item.Contains('<') ? '<' : ',')+1));
                        fullName = item.Substring(item.IndexOf(item.Contains('<') ? '<' : ',') + 1);
                    }
                    else
                    {
                        stringBuilder.Append(item);
                    }
                }
                else
                {
                    fullName = $"{fullName}.{item}";
                }
            }

            return stringBuilder.ToString();
        }

        private static void AddUsingNamespace(List<string> usingNamespaceList, string typeName)
        {
            var rootNamespace = $"using {GetTypeNamespace(typeName)};";
            if (usingNamespaceList.Contains(rootNamespace))
            {
                return;
            }

            usingNamespaceList.Add(rootNamespace);
        }

        private string NormalizeTypeName(string typeName)
        {
            typeName = typeName switch
            {
                "Void" => "void",
                "Boolean" => "bool",
                "String" => "string",
                "Int32" => "int",
                "Int64" => "long",
                "Double" => "double",
                "Object" => "object",
                "Byte" => "byte",
                "Char" => "char",
                _ => typeName
            };

            return typeName;
        }

        private static string CheckWorkDirectory(string directory)
        {
            if (!Directory.Exists(directory))
            {
                throw new CliUsageException("Specified directory does not exist.");
            }

            var projectFiles = Directory.GetFiles(directory, "*HttpApi.Client.csproj");
            if (!projectFiles.Any())
            {
                throw new CliUsageException(
                    "No project file found in the directory. The working directory must have a HttpApi.Client project file.");
            }

            return projectFiles.First();
        }

        private static void CheckFolder(string folder)
        {
            if (!folder.IsNullOrWhiteSpace() && Path.HasExtension(folder))
            {
                throw new CliUsageException("Option folder should be a directory.");
            }
        }

        private Type GetStartupModule(string assemblyPath)
        {
            return Assembly
                .LoadFrom(assemblyPath)
                .GetTypes()
                .SingleOrDefault(AbpModule.IsAbpModule);
        }

        private string GetRootNamespace(string projectFilePath)
        {
            var document = new XmlDocument();
            document.Load(projectFilePath);

            var rootNamespace = document.SelectSingleNode("//RootNamespace")?.InnerText;

            if(rootNamespace.IsNullOrWhiteSpace())
            {
                rootNamespace = Path.GetFileNameWithoutExtension(projectFilePath);
            }

            return rootNamespace;
        }
    }
}
