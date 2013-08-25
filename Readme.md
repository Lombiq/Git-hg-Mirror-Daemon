# Readme



## Installation on the server

1. Install the latest TortoiseHg.
2. Install the hg-git Mercurial extensions and enable it as described on [its home page](http://hg-git.github.io/).
3. Copy the service exe onto the server in a desired location.
4. Run the exe as administrator. This will install the service (running it again uninstalls it). Verify if the installation was successful by checking Services.
5. The service is set to automatic start, i.e. it will start with Windows. The first time however it should be manually started from Services.


## Usage notes

The service writes log messages to the Windows event log. You can view the entries in the Windows Event Viewer under "Applications and Services Logs" in the log "Git-hg Mirror Daemon".