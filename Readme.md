# Readme



## Setting up Mercurial

This is needed on the server as well as locally if you want to test mirroring.

1. Install the latest [TortoiseHg](http://tortoisehg.bitbucket.org/).
2. Enable the hggit extension (if it wasn't already; it comes with TortoiseHg). 
2. Make sure that the mercurial_keyring extension is **not** enabled and there are no prefixes configured in the `[auth]` section of mercurial.ini (as these will override the authentication configs used during git interactions).
3. Restart TortoiseHg.
4. Install [Git](https://git-scm.com/) (if you have GitExtensions already installed, you can skip this step). During installation select the option "Use Git from the Windows Command Prompt"; everything else can be the default.


## Installation on the server

1. 
2. Set up Mercurial and Git as described above.
3. Copy the service exe and its dependencies (basically the whole Release bin folder) onto the server in a desired location (e.g. C:\GitHgMirror\GitHgMirrorDaemon).
4. Run the exe as administrator. This will install the service (running it again uninstalls it). Verify if the installation was successful by checking Services.
5. Set up the service to run as the local user from under Properties/Log On. This makes it possible for the service to use the same settings what you see in TortoiseHg.
5. The service is set to automatic start, i.e. it will start with Windows. The first time however it should be manually started from Services.


## Usage notes

The service writes log messages to the Windows event log. You can view the entries in the Windows Event Viewer under "Applications and Services Logs" in the log "Git-hg Mirror Daemon".