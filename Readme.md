# Readme



## Setting up Mercurial and Git

This is needed on the server as well as locally if you want to test mirroring.

1. Install the latest [TortoiseHg](http://tortoisehg.bitbucket.org/).
2. Enable the hggit extension (if it wasn't already; it comes with TortoiseHg). 
2. Make sure that the mercurial_keyring extension is **not** enabled and there are no prefixes configured in the `[auth]` section of mercurial.ini (as these will override the authentication configs used during git interactions).
3. Restart TortoiseHg.
4. Install [Git](https://git-scm.com/) (if you have GitExtensions already installed, you can skip this step). During installation select the option "Use Git from the Windows Command Prompt"; everything else can be the default.


## Installation on the server

1. Set up Mercurial and Git as described above.
2. Copy the service exe and its dependencies (basically the whole Release bin folder) onto the server in a desired location (e.g. C:\GitHgMirror\GitHgMirrorDaemon).
3. Run the exe as administrator. This will install the service (running it again uninstalls it). Verify if the installation was successful by checking Services.
4. Set up the service to run as the local user from under Properties/Log On. This makes it possible for the service to use the same settings what you see in TortoiseHg.
5. The service is set to automatic start, i.e. it will start with Windows. The first time however it should be manually started from Services.


## Troubleshooting

The service writes log messages to the Windows event log. You can view the entries in the Windows Event Viewer under "Applications and Services Logs" in the log "Git-hg Mirror Daemon".

If you get "mercurial abort: error: [SSL: CERTIFICATE_VERIFY_FAILED] certificate verify failed (_ssl.c:581)"." or similar errors then it means that Mercurial couldn't verify the SSL certificates of remote servers. To work around this set fingerprints like following in Mercurial.ini for all hosts:

    [hostfingerprints]
    bitbucket.org = 46:DE:34:E7:9B:18:CD:7F:AE:FD:8B:E3:BC:F4:1A:5E:38:D7:AC:24
    github.com = A0:C4:A7:46:00:ED:A7:2D:C0:BE:CB:9A:8C:B6:07:CA:58:EE:74:5E

**Fingerprints should be all uppercase!**


## Local testing

If you want to test the Daemon locally just run `GitHgMirror.Tester`; make sure to do the setup as described above first.

You can configure some settings in `GitHgMirror.Tester.Program`. If you want to test the mirroring of a specific config you can add the following to the constructor of `MirrorRunner` after the initial setup:

    using (var mirror = new Mirror(_settings, _eventLog))
    {
        var configuration = new MirroringConfiguration
        {
            Direction = MirroringDirection.GitToHg,
            GitCloneUri = new Uri("git+https://github.com/path-to-git-repo.git"),
            HgCloneUri = new Uri("https://LombiqBot:password@bitbucket.org/path-to-hg-repo")
        };
        mirror.MirrorRepositories(configuration);
    }

Of course make sure not to commit such tests (to the dev branch).