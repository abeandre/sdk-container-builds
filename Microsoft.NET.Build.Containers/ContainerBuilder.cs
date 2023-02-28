// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.NET.Build.Containers.Outputs;
using Microsoft.NET.Build.Containers.Resources;

namespace Microsoft.NET.Build.Containers;

public static class ContainerBuilder
{
    public static async Task Containerize(DirectoryInfo folder, string workingDir, string registryName, string baseName, string baseTag, string[] entrypoint, string[] entrypointArgs, string imageName, string[] imageTags, string? outputRegistry, string[] labels, Port[] exposedPorts, string[] envVars, string containerRuntimeIdentifier, string ridGraphPath, string localContainerDaemon, string tarFilePath)
    {
        var isDaemonPull = String.IsNullOrEmpty(registryName);
        if (isDaemonPull)
        {
            throw new NotSupportedException(Resource.GetString(nameof(Strings.DontKnowHowToPullImages)));
        }

        Registry baseRegistry = new Registry(ContainerHelpers.TryExpandRegistryToUri(registryName));
        ImageReference sourceImageReference = new(baseRegistry, baseName, baseTag);
        var isDockerPush = String.IsNullOrEmpty(outputRegistry);
        var destinationImageReferences = imageTags.Select(t => new ImageReference(isDockerPush ? null : new Registry(ContainerHelpers.TryExpandRegistryToUri(outputRegistry!)), imageName, t));

        var isFileExport = !String.IsNullOrEmpty(tarFilePath);

        ImageBuilder imageBuilder = await baseRegistry.GetImageManifest(baseName, baseTag, containerRuntimeIdentifier, ridGraphPath).ConfigureAwait(false);

        imageBuilder.SetWorkingDirectory(workingDir);

        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
        };

        Layer l = Layer.FromDirectory(folder.FullName, workingDir);

        imageBuilder.AddLayer(l);

        imageBuilder.SetEntryPoint(entrypoint, entrypointArgs);

        foreach (string label in labels)
        {
            string[] labelPieces = label.Split('=');

            // labels are validated by System.CommandLine API
            imageBuilder.AddLabel(labelPieces[0], labelPieces[1]);
        }

        foreach (string envVar in envVars)
        {
            string[] envPieces = envVar.Split('=', 2);

            imageBuilder.AddEnvironmentVariable(envPieces[0], envPieces[1]);
        }

        foreach ((int number, PortType type) in exposedPorts)
        {
            // ports are validated by System.CommandLine API
            imageBuilder.ExposePort(number, type);
        }

        BuiltImage builtImage = imageBuilder.Build();

        foreach (var destinationImageReference in destinationImageReferences)
        {
            if (destinationImageReference.Registry is { } outReg)
            {
                try
                {
                    outReg.Push(builtImage, sourceImageReference, destinationImageReference, (message) => Console.WriteLine($"Containerize: {message}")).Wait();
                    Console.WriteLine($"Containerize: Pushed container '{destinationImageReference.RepositoryAndTag}' to registry '{outputRegistry}'");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Containerize: error CONTAINER001: Failed to push to output registry: {e.Message}");
                    Environment.ExitCode = 1;
                }
            }
            else
            {
                if (isFileExport)
                {
                    var fileExporter = new FileOutput(Console.WriteLine);
                    fileExporter.Export(tarFilePath, builtImage, sourceImageReference, destinationImageReference).Wait();
                    Console.WriteLine("Containerize: File '{0}' created ", tarFilePath);
                    return;
                }

                var localDaemon = GetLocalDaemon(localContainerDaemon, Console.WriteLine);
                if (!(await localDaemon.IsAvailable().ConfigureAwait(false)))
                {
                    Console.WriteLine("Containerize: error CONTAINER007: The Docker daemon is not available, but pushing to a local daemon was requested. Please start Docker and try again.");
                    Environment.ExitCode = 7;
                    return;
                }
                try
                {
                    localDaemon.Load(builtImage, sourceImageReference, destinationImageReference).Wait();
                    Console.WriteLine("Containerize: Pushed container '{0}' to Docker daemon", destinationImageReference.RepositoryAndTag);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Containerize: error CONTAINER001: Failed to push to local docker registry: {e.Message}");
                    Environment.ExitCode = 1;
                }
            }
        }
    }

    private static LocalDocker GetLocalDaemon(string localDaemonType, Action<string> logger)
    {
        var daemon = localDaemonType switch
        {
            KnownDaemonTypes.Docker => new LocalDocker(logger),
            _ => throw new ArgumentException($"Unknown local container daemon type '{localDaemonType}'. Valid local container daemon types are {String.Join(",", KnownDaemonTypes.SupportedLocalDaemonTypes)}", nameof(localDaemonType))
        };
        return daemon;
    }
}
