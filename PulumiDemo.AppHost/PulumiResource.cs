using System.Collections.Immutable;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulumi.Automation;

public class PulumiResource(string name, PulumiFn program, Action<IDictionary<string, ConfigValue>>? configure)
    : Resource(name)
{
    public PulumiFn Program { get; } = program;

    public Action<IDictionary<string, ConfigValue>>? Configure { get; } = configure;

    public IImmutableDictionary<string, OutputValue>? Outputs { get; set; }

    internal void WriteToManifest(ManifestPublishingContext context)
    {
        context.Writer.WriteString("type", "pulimistack.v0");
    }
}

public class PulumiOutputReference(string name, PulumiResource resource)
{
    public string Name { get; } = name;
    public PulumiResource Resource { get; } = resource;

    public object? Value
    {
        get
        {
            if (Resource.Outputs == null)
            {
                throw new InvalidOperationException("Pulumi stack outputs are not available yet");
            }

            if (!Resource.Outputs.TryGetValue(Name, out var value))
            {
                throw new InvalidOperationException($"Pulumi stack does not have an output named '{Name}'");
            }

            return value.Value;
        }
    }

    public string ValueExpression => $"{{{Resource.Name}.outputs.{Name}}}";
}

public static class PulumiExtensions
{
    public static IResourceBuilder<PulumiResource> AddPulumiStack<TStack>(this IDistributedApplicationBuilder builder, string name, Action<IDictionary<string, ConfigValue>>? configure = null)
        where TStack : Pulumi.Stack, new()
    {
        builder.Services.TryAddLifecycleHook<PulumiLifecycleHook>();

        configure = GetConfigure(builder.Configuration, "Pulumi:Stacks:" + name, configure);

        var resource = new PulumiResource(name, PulumiFn.Create<TStack>(), configure);
        return builder.AddResource(resource)
                      .WithManifestPublishingCallback(resource.WriteToManifest);
    }

    public static IResourceBuilder<PulumiResource> AddPulumi(this IDistributedApplicationBuilder builder, string name, Func<IDictionary<string, object?>> program, Action<IDictionary<string, ConfigValue>>? configure = null)
    {
        builder.Services.TryAddLifecycleHook<PulumiLifecycleHook>();

        configure = GetConfigure(builder.Configuration, "Pulumi:Stacks:" + name, configure);

        var resource = new PulumiResource(name, PulumiFn.Create(program), configure);
        return builder.AddResource(resource)
                      .WithManifestPublishingCallback(resource.WriteToManifest);
    }

    public static IResourceBuilder<PulumiResource> AddPulumi(this IDistributedApplicationBuilder builder, string name, Action program, Action<IDictionary<string, ConfigValue>>? configure = null)
    {
        builder.Services.TryAddLifecycleHook<PulumiLifecycleHook>();

        configure = GetConfigure(builder.Configuration, "Pulumi:Stacks:" + name, configure);

        var resource = new PulumiResource(name, PulumiFn.Create(program), configure);
        return builder.AddResource(resource)
                      .WithManifestPublishingCallback(resource.WriteToManifest);
    }

    private static Action<IDictionary<string, ConfigValue>>? GetConfigure(IConfiguration configuration, string section, Action<IDictionary<string, ConfigValue>>? configure)
    {
        var sectionConfiguration = configuration.GetSection(section);

        if (!sectionConfiguration.Exists())
        {
            return configure;
        }

        return config =>
        {
            // Read from configuration then call the configure action
            foreach (var child in sectionConfiguration.AsEnumerable())
            {
                if (child.Value is null)
                {
                    continue;
                }

                config[child.Key[(section.Length + 1)..]] = new ConfigValue(child.Value);
            }

            configure?.Invoke(config);
        };
    }

    public static PulumiOutputReference GetOutput(this IResourceBuilder<PulumiResource> builder, string name)
    {
        return new PulumiOutputReference(name, builder.Resource);
    }

    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, string name, PulumiOutputReference value)
        where T : IResourceWithEnvironment
    {
        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[name] = context.ExecutionContext.Operation switch
            {
                DistributedApplicationOperation.Publish => value.ValueExpression,
                _ => value.Value?.ToString()!
            };
        });
    }
}

internal class PulumiLifecycleHook(IHostEnvironment environment, 
    DistributedApplicationExecutionContext executionContext,
    ILogger<PulumiLifecycleHook> logger) 
    : IDistributedApplicationLifecycleHook
{
    public async Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
    {
        // Skip pulumi operations during publish
        if (executionContext.Operation == DistributedApplicationOperation.Publish)
        {
            return;
        }

        foreach (var pulumiResource in appModel.Resources.OfType<PulumiResource>())
        {
            var stackArgs = new InlineProgramArgs(environment.ApplicationName, pulumiResource.Name, pulumiResource.Program)
            {
                Logger = logger
            };

            var stack = await LocalWorkspace.CreateOrSelectStackAsync(stackArgs, cancellationToken);

            var configuration = new Dictionary<string, ConfigValue>();

            pulumiResource.Configure?.Invoke(configuration);

            await stack.Workspace.SetAllConfigAsync(pulumiResource.Name, configuration, cancellationToken);

            var result = await stack.UpAsync(new() { Logger = logger }, cancellationToken: cancellationToken);

            pulumiResource.Outputs = result.Outputs;
        }
    }
}