var builder = DistributedApplication.CreateBuilder(args);

var stack = builder.AddPulumiStack<MyStack>("dev");

builder.AddProject<Projects.PulumiDemo_Api>("api")
    .WithEnvironment("StorageEndpoint", stack.GetOutput("BlobEndpoint"));

builder.Build().Run();
