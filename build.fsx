#r "paket:
storage: packages
nuget Fake.IO.FileSystem
nuget Fake.DotNet.Cli
nuget Fake.Core.Target
nuget Fake.Core.ReleaseNotes
nuget Fake.Tools.Git
nuget Fake.JavaScript.Yarn
nuget Fake.DotNet.FSFormatting //"
#if !FAKE
#load ".fake/build.fsx/intellisense.fsx"
#r "Facades/netstandard"
#endif

open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.Tools
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.JavaScript
open System.IO


// Filesets
let projects  =
    !! "src/**.fsproj"
    ++ "netstandard/**.fsproj"


let withWorkDir = DotNet.Options.withWorkingDirectory

Target.create "Clean" (fun _ ->
    Shell.cleanDir "src/obj"
    Shell.cleanDir "src/bin"
    Shell.cleanDir "netstandard/obj"
    Shell.cleanDir "netstandard/bin"
)

Target.create "Restore" (fun _ ->
    projects
    |> Seq.iter (fun s ->
        let dir = Path.GetDirectoryName s
        DotNet.restore (fun a -> a.WithCommon (withWorkDir dir)) s
    )
    Yarn.install id
)

Target.create "Build" (fun _ ->
    projects
    |> Seq.iter (fun s ->
        let dir = Path.GetDirectoryName s
        DotNet.build (fun a ->
            a.WithCommon
                (fun c ->
                    let c = c |> withWorkDir dir
                    {c with CustomParams = Some "/p:SourceLinkCreate=true"}))
            s
    )
)

Target.create "Test" (fun _ ->
    DotNet.test (fun a -> a.WithCommon id) "netstandard.tests"
    Yarn.exec "test" id
)

let release = ReleaseNotes.load "RELEASE_NOTES.md"

Target.create "Meta" (fun _ ->
    [ "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">"
      "<PropertyGroup>"
      "<PackageProjectUrl>https://github.com/elmish/elmish</PackageProjectUrl>"
      "<PackageLicenseUrl>https://raw.githubusercontent.com/elmish/elmish/master/LICENSE.md</PackageLicenseUrl>"
      "<PackageIconUrl>https://raw.githubusercontent.com/elmish/elmish/master/docs/files/img/logo.png</PackageIconUrl>"
      "<RepositoryUrl>https://github.com/elmish/elmish.git</RepositoryUrl>"
      sprintf "<PackageReleaseNotes>%s</PackageReleaseNotes>" (List.head release.Notes)
      "<PackageTags>fable;elm;fsharp</PackageTags>"
      "<Authors>Eugene Tolmachev</Authors>"
      sprintf "<Version>%s</Version>" (string release.SemVer)
      "</PropertyGroup>"
      "</Project>"]
    |> File.write false "Directory.Build.props"
)

// --------------------------------------------------------------------------------------
// Build a NuGet package

Target.create "Package" (fun _ ->
    projects
    |> Seq.iter (fun s ->
        let dir = Path.GetDirectoryName s
        DotNet.pack (fun a ->
            a.WithCommon (withWorkDir dir)
        ) s
    )
)

Target.create "PublishNuget" (fun _ ->
    let exec dir =
        DotNet.exec (fun a ->
            a.WithCommon (withWorkDir dir)
        )

    let args = sprintf "push Fable.Elmish.%s.nupkg -s nuget.org -k %s" (string release.SemVer) (Environment.environVar "nugetkey")
    let result = exec "src/bin/Release" "nuget" args
    if (not result.OK) then failwithf "%A" result.Errors

    let args = sprintf "push Elmish.%s.nupkg -s nuget.org -k %s" (string release.SemVer) (Environment.environVar "nugetkey")
    let result = exec "netstandard/bin/Release" "nuget" args
    if (not result.OK) then
        failwithf "%A" result.Errors
)


// --------------------------------------------------------------------------------------
// Generate the documentation
let gitName = "elmish"
let gitOwner = "elmish"
let gitHome = sprintf "https://github.com/%s" gitOwner
let gitRepo = sprintf "git@github.com:%s/%s.git" gitOwner gitName
let docs_out = "docs/output"
let docsHome = "https://elmish.github.io/elmish"

let copyFiles() =
    let header =
        Fake.Core.String.splitStr "\n" """(*** hide ***)
#I "../../src/bin/Release/netstandard2.0"
#r "Fable.Core.dll"
#r "Fable.Elmish.dll"

(**
*)"""

    !!"src/*.fs"
    |> Seq.map (fun fn -> File.read fn |> Seq.append header, fn)
    |> Seq.iter (fun (lines,fn) ->
        let fsx = Path.Combine("docs/content",Path.ChangeExtension(fn |> Path.GetFileName, "fsx"))
        lines |> File.writeNew fsx)

let generateDocs _ =
    copyFiles()
    let info =
      [ "project-name", "elmish"
        "project-author", "Eugene Tolmachev"
        "project-summary", "Elm-like Cmd and Program modules for F# apps"
        "project-github", sprintf "%s/%s" gitHome gitName
        "project-nuget", "http://nuget.org/packages/Fable.Elmish" ]

    FSFormatting.createDocs (fun args ->
            { args with
                Source = "docs/content"
                OutputDirectory = docs_out
                LayoutRoots = [ "docs/tools/templates"
                                ".fake/build.fsx/packages/fsharp.formatting/templates" ]
                ProjectParameters  = ("root", docsHome)::info
                Template = "docpage.cshtml" } )

Target.create "GenerateDocs" generateDocs


Target.create "WatchDocs" (fun _ ->
    use watcher =
        (!! "docs/content/**/*.*")
        |> ChangeWatcher.run generateDocs

    Trace.traceImportant "Waiting for help edits. Press any key to stop."

    System.Console.ReadKey() |> ignore

    watcher.Dispose()
)

// --------------------------------------------------------------------------------------
// Release Scripts

Target.create "ReleaseDocs" (fun _ ->
    let tempDocsDir = "temp/gh-pages"
    Shell.cleanDir tempDocsDir
    Git.Repository.cloneSingleBranch "" gitRepo "gh-pages" tempDocsDir

    Shell.copyRecursive docs_out tempDocsDir true |> Trace.tracefn "%A"
    Git.Staging.stageAll tempDocsDir
    Git.Commit.exec tempDocsDir (sprintf "Update generated documentation for version %s" release.NugetVersion)
    Git.Branches.push tempDocsDir
)

Target.create "Publish" ignore

// Build order
"Clean"
  ==> "Meta"
  ==> "Restore"
  ==> "Build"
  ==> "Test"
  ==> "Package"
  ==> "GenerateDocs"
  ==> "PublishNuget"
  ==> "ReleaseDocs"
  ==> "Publish"


// start build
Target.runOrDefault "Build"
