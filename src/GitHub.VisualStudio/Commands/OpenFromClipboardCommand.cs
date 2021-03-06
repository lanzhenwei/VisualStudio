﻿using System;
using System.ComponentModel.Composition;
using GitHub.Commands;
using GitHub.Exports;
using GitHub.Services;
using GitHub.Services.Vssdk.Commands;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace GitHub.VisualStudio.Commands
{
    [Export(typeof(IOpenFromClipboardCommand))]
    public class OpenFromClipboardCommand : VsCommand<string>, IOpenFromClipboardCommand
    {
        public const string NoGitHubUrlMessage = "Couldn't a find a GitHub URL in clipboard";
        public const string NoResolveSameOwnerMessage = "Couldn't find target URL in current repository. Try again after doing a fetch.";
        public const string NoResolveDifferentOwnerMessage = "The target URL has a different owner to the current repository.";
        public const string NoActiveRepositoryMessage = "There is no active repository to navigate";
        public const string ChangesInWorkingDirectoryMessage = "This file has changed since the permalink was created";
        public const string DifferentRepositoryMessage = "Please open the repository '{0}' and try again";
        public const string UnknownLinkTypeMessage = "Couldn't open from '{0}'. Only URLs that link to repository files are currently supported.";

        readonly Lazy<IGitHubContextService> gitHubContextService;
        readonly Lazy<ITeamExplorerContext> teamExplorerContext;
        readonly Lazy<IVSServices> vsServices;
        readonly Lazy<UIContext> uiContext;

        /// <summary>
        /// Gets the GUID of the group the command belongs to.
        /// </summary>
        public static readonly Guid CommandSet = Guids.guidGitHubCmdSet;

        /// <summary>
        /// Gets the numeric identifier of the command.
        /// </summary>
        public const int CommandId = PkgCmdIDList.openFromClipboardCommand;

        [ImportingConstructor]
        public OpenFromClipboardCommand(
            Lazy<IGitHubContextService> gitHubContextService,
            Lazy<ITeamExplorerContext> teamExplorerContext,
            Lazy<IVSServices> vsServices)
            : base(CommandSet, CommandId)
        {
            this.gitHubContextService = gitHubContextService;
            this.teamExplorerContext = teamExplorerContext;
            this.vsServices = vsServices;

            // See https://code.msdn.microsoft.com/windowsdesktop/AllowParams-2005-9442298f
            ParametersDescription = "u";    // accept a single url

            // This command is only visible when in the context of a Git repository
            uiContext = new Lazy<UIContext>(() => UIContext.FromUIContextGuid(new Guid(Guids.UIContext_Git)));
        }

        public override async Task Execute(string url)
        {
            var context = gitHubContextService.Value.FindContextFromClipboard();
            if (context == null)
            {
                vsServices.Value.ShowMessageBoxInfo(NoGitHubUrlMessage);
                return;
            }

            if (context.LinkType != LinkType.Blob)
            {
                var message = string.Format(UnknownLinkTypeMessage, context.Url);
                vsServices.Value.ShowMessageBoxInfo(message);
                return;
            }

            var activeRepository = teamExplorerContext.Value.ActiveRepository;
            var repositoryDir = activeRepository?.LocalPath;
            if (repositoryDir == null)
            {
                vsServices.Value.ShowMessageBoxInfo(NoActiveRepositoryMessage);
                return;
            }

            if (!string.Equals(activeRepository.Name, context.RepositoryName, StringComparison.OrdinalIgnoreCase))
            {
                vsServices.Value.ShowMessageBoxInfo(string.Format(DifferentRepositoryMessage, context.RepositoryName));
                return;
            }

            var (commitish, path, isSha) = gitHubContextService.Value.ResolveBlob(repositoryDir, context);
            if (path == null)
            {
                if (!string.Equals(activeRepository.Owner, context.Owner, StringComparison.OrdinalIgnoreCase))
                {
                    vsServices.Value.ShowMessageBoxInfo(NoResolveDifferentOwnerMessage);
                }
                else
                {
                    vsServices.Value.ShowMessageBoxInfo(NoResolveSameOwnerMessage);
                }

                return;
            }

            var hasChanges = gitHubContextService.Value.HasChangesInWorkingDirectory(repositoryDir, commitish, path);
            if (hasChanges)
            {
                // AnnotateFile expects a branch name so we use the current branch
                var branchName = activeRepository.CurrentBranch.Name;

                if (await gitHubContextService.Value.TryAnnotateFile(repositoryDir, branchName, context))
                {
                    return;
                }

                vsServices.Value.ShowMessageBoxInfo(ChangesInWorkingDirectoryMessage);
            }

            gitHubContextService.Value.TryOpenFile(repositoryDir, context);
        }

        protected override void QueryStatus()
        {
            Visible = uiContext.Value.IsActive;
        }
    }
}
