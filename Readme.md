# Git-hg Mirror Daemon readme



Two-way Git-Mercurial repository syncing Windows service used by [Git-hg Mirror](https://githgmirror.com). The frontend component is [Git-Hg Mirror Common](https://github.com/Lombiq/Git-Hg-Mirror-Common).

This is a C# project that you'll need Visual Studio to work with. Commits in the master/default branch represent deployments, i.e. the latest commit in that branch shows the version currently running in production.

The project's source is available in two public source repositories, automatically mirrored in both directions with Git-hg Mirror itself:

- [https://bitbucket.org/Lombiq/git-hg-mirror-daemon](https://bitbucket.org/Lombiq/git-hg-mirror-daemon) (Mercurial repository)
- [https://github.com/Lombiq/Git-hg-Mirror-Daemon](https://github.com/Lombiq/Git-hg-Mirror-Daemon) (Git repository)

Bug reports, feature requests and comments are warmly welcome, **please do so via GitHub**. Feel free to send pull requests too, no matter which source repository you choose for this purpose.

This project is developed by [Lombiq Technologies Ltd](https://lombiq.com/). Commercial-grade support is available through Lombiq.


## Setting up Mercurial and Git

This is needed on the server as well as locally if you want to test mirroring.

1. Install [TortoiseHg](https://tortoisehg.bitbucket.io/download/index.html); [v5.0.2](https://www.mercurial-scm.org/release/tortoisehg/windows/tortoisehg-5.0.2-x64.msi) is tested, newer may work too.
2. Install [Git](https://git-scm.com/); [v2.30.1](https://github.com/git-for-windows/git/releases/download/v2.12.1.windows.1/Git-2.12.1-64-bit.exe) is tested, newer may work too (if you have GitExtensions already installed, you can skip this step). During installation select the option "Use Git from the Windows Command Prompt"; everything else can be the default.


## Installation on the server

1. Set up Mercurial and Git as described above.
2. Copy the service exe and its dependencies (basically the whole Release bin folder) onto the server in a desired location (e.g. *C:\GitHgMirror\GitHgMirrorDaemon*).
3. Configure the service in the *GitHgMirror.Daemon.exe.config* file (for documentation on these take a look at the `GitHgMirror.Daemon.Constants` class), at least set a custom API password. This should be the same as what's configured in the frontend component. If you're updating the service on an already running server then make sure not to overwrite the config file to keep the settings.
4. Unless you want Windows Defender to check every repository for viruses exclude the repository directory (e.g. _C:\GitHgMirror\Repositories_) and the _GitHgMirror.Daemon.exe_, _hg.exe_ and _git.exe_ processes from scanning.
5. Run the exe as administrator. This will install the service (running it again uninstalls it). Verify if the installation was successful by checking Services.
6. Set up the service to run as the local user from under Properties/Log On. This makes it possible for the service to access the full SSL certificate store (thus preventing e.g. Let's Encrypt certificates being mistakenly flagged as invalid) and use the same settings what you see in TortoiseHg (though that shouldn't be necessary unless you're testing configs or want to configure host fingerprints as in the next step).
7. Configure the following section in mercurial.ini:

    [hostfingerprints]
    riverbankcomputing.com = 55:37:0F:68:9D:76:61:32:A2:87:EE:67:5B:66:AD:9D:53:7F:77:03

8. The service is set to automatic start, i.e. it will start with Windows. The first time however it should be manually started from Services.


## Troubleshooting

### Troubleshooting Mercurial errors
You can enable debug settings for Mercurial via `MirroringSettings.MercurialSettings` to help troubleshoot issues with e.g. the debug output.

### Logging
The service writes log messages to the Windows event log. You can view the entries in the Windows Event Viewer under "Applications and Services Logs" in the log "Git-hg Mirror Daemon".

### SSL fingerprint errors
If you get `mercurial abort: error: [SSL: CERTIFICATE_VERIFY_FAILED] certificate verify failed (_ssl.c:581)` or similar errors then it means that Mercurial couldn't verify the SSL certificates of remote servers. To work around this set fingerprints like following in Mercurial.ini for all affected hosts:

    [hostfingerprints]
    bitbucket.org = ‎3F:D3:C5:17:23:3C:CD:F5:2D:17:76:06:93:7E:EE:97:42:21:14:AA
    github.com = A0:C4:A7:46:00:ED:A7:2D:C0:BE:CB:9A:8C:B6:07:CA:58:EE:74:5E

**Fingerprints should be all uppercase!**

Note that since TortoiseHg 3.5.2 such errors seem not to happen with Bitbucket and GitHub so adding such fingerprints shouldn't be required.

### "EOF occurred in violation of protocol" errors
If you get `EOF occurred in violation of protocol (_ssl.c:581)` or similar errors when a remote operation is done on a repository then you have an outdated version of Mercurial: you need at least Mercurial 3.6.1 (bundled with TortoiseHg 3.6.1).

There is re-try logic in place specifically for such errors so while these make syncing slower they shouldn't cause syncing to fail (within certain bounds: if there are too many retries needed then yes). Nevertheless if you can, update Mercurial.

### Finding out which repo an hg.exe process works on
There will be an *hg.exe* (Mercurial command line) instance started for each mirroring operation. If you want to find out which repo a *hg.exe* instance works on (because e.g. it hogs the CPU for hours) then you can find out with [Process Explorer](https://technet.microsoft.com/en-us/sysinternals/bb896653.aspx): open the properties of the process in question and there under the Image tab you'll see the command line parameters it was started with. This will tell you which repo it processes.


## Local testing

If you want to test the Daemon locally just run `GitHgMirror.Tester`; make sure to do the setup as described above first.

You can configure some settings in `GitHgMirror.Tester.Program`. If you want to test the mirroring of a specific config you can add the following to the constructor of `MirrorRunner` after the initial setup:

    using (var mirror = new Mirror(_eventLog))
    {
        var configuration = new MirroringConfiguration
        {
            Direction = MirroringDirection.GitToHg,
            GitCloneUri = new Uri("git+https://github.com/path-to-git-repo.git"),
            HgCloneUri = new Uri("https://LombiqBot:password@bitbucket.org/path-to-hg-repo"),
        };
        mirror.MirrorRepositories(configuration, _settings, _cancellationTokenSource.Token);
    }

Of course make sure not to commit such tests (at least not to the dev branch).
