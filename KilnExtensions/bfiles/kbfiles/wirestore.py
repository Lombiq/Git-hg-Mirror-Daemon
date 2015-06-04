'''Bfile store working over mercurial's wire protocol'''

import remotestore

class wirestore(remotestore.remotestore):
    def __init__(self, ui, repo, remote):
        cap = remote.capable('bfilestore')
        if not cap:
            raise remotestore.storeprotonotcapable([])
        storetypes = cap.split(',')
        if not 'serve' in storetypes:
            raise remotestore.storeprotonotcapable(storetypes)
        self.remote = remote
        super(wirestore, self).__init__(ui, repo, remote.url())

    def _put(self, hash, fd):
        return self.remote.putbfile(hash, fd)

    def _get(self, hash):
        return self.remote.getbfile(hash)

    def _stat(self, hash):
        return self.remote.statbfile(hash)
