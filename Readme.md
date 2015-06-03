# Readme



## Installation on the server

1. Install the latest TortoiseHg.
2. Enable the hggit extension (it comes with TortoiseHg) and restart TortoiseHg. Make sure that the mercurial_keyring extension is **not** enabled and there are no prefixes configured in the `[auth]` section of mercurial.ini (as these will override the authentication configs used during git interactions)
3. Copy the service exe and its dependencies (basically the whole Release bin folder) onto the server in a desired location (e.g. C:\GitHgMirror\GitHgMirrorDaemon).
4. Run the exe as administrator. This will install the service (running it again uninstalls it). Verify if the installation was successful by checking Services.
5. Set up the service to run as the local user from under Properties/Log On. This makes it possible for the service to use the same settings what you see in TortoiseHg.
5. The service is set to automatic start, i.e. it will start with Windows. The first time however it should be manually started from Services.


## Usage notes

The service writes log messages to the Windows event log. You can view the entries in the Windows Event Viewer under "Applications and Services Logs" in the log "Git-hg Mirror Daemon".