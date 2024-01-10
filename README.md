# Archive to Virtual Drive

This project is built on top of [dokany](https://github.com/dokan-dev/dokany/) and 
[dokan-dotnet](https://github.com/dokan-dev/dokan-dotnet), which are Windows user mode file system drivers.

Before using this project, you need to install dokany [v2.1.0.1000](https://github.com/dokan-dev/dokany/releases/tag/v2.1.0.1000)

**Windows x64 is the only supported platform**.

## Usage
```shell
cd <directory of zip2vd.cli.exe> --very important
zip2vd.cli.exe --FilePath <path to zip file> --MountPath <path to mount point>
e.g. zip2vd.cli.exe --FilePath D:\test.zip --MountPath R:\
```

## Build
This project is built with .NET 8 and the dependencies are managed by Nuget. You should be able to build it with simple
`dotnet build` command.