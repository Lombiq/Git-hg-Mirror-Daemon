# Readme



## Installation on the server

1. Install the latest TortoiseHg.
2. Install the hg-git Mercurial extensions and enable it as described on [its home page](http://hg-git.github.io/). With the latest version of TortoiseHg Bookmarks doesn't need to be enabled.
3. Copy the service exe and its dependencies (basically the whole Release bin folder) onto the server in a desired location.
4. Run the exe as administrator. This will install the service (running it again uninstalls it). Verify if the installation was successful by checking Services.
5. Set up the service to run as the local user from under Properties/Log On. This makes it possible for the service to use the same settings what you see in TortoiseHg.
5. The service is set to automatic start, i.e. it will start with Windows. The first time however it should be manually started from Services.


## Usage notes

The service writes log messages to the Windows event log. You can view the entries in the Windows Event Viewer under "Applications and Services Logs" in the log "Git-hg Mirror Daemon".