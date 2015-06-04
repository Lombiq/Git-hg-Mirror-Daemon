'''Store class for local filesystem.'''

import os

from mercurial import util
from mercurial.i18n import _

import bfutil
import basestore

class localstore(basestore.basestore):
    '''Because there is a system wide cache, the local store always uses that
    cache.  Since the cache is updated elsewhere, we can just read from it here
    as if it were the store.'''

    def __init__(self, ui, repo, remote):
        url = os.path.join(remote.path, '.hg', bfutil.longname)
        super(localstore, self).__init__(ui, repo, util.expandpath(url))

    def put(self, source, filename, hash):
        '''Any file that is put must already be in the system wide cache so do
        nothing.'''
        return

    def exists(self, hash):
        return bfutil.insystemcache(self.repo.ui, hash)

    def _getfile(self, tmpfile, filename, hash):
        if bfutil.insystemcache(self.ui, hash):
            return bfutil.systemcachepath(self.ui, hash)
        raise basestore.StoreError(filename, hash, '',
            _("Can't get file locally"))

    def _verifyfile(self, cctx, cset, contents, standin, verified):
        filename = bfutil.splitstandin(standin)
        if not filename:
            return False
        fctx = cctx[standin]
        key = (filename, fctx.filenode())
        if key in verified:
            return False

        expecthash = fctx.data()[0:40]
        verified.add(key)
        if not bfutil.insystemcache(self.ui, expecthash):
            self.ui.warn(
                _('changeset %s: %s missing\n'
                  '  (%s: %s)\n')
                % (cset, filename, expecthash, err.strerror))
            return True                 # failed

        if contents:
            storepath = bfutil.systemcachepath(self.ui, expecthash)
            actualhash = bfutil.hashfile(storepath)
            if actualhash != expecthash:
                self.ui.warn(
                    _('changeset %s: %s: contents differ\n'
                      '  (%s:\n'
                      '  expected hash %s,\n'
                      '  but got %s)\n')
                    % (cset, filename, storepath, expecthash, actualhash))
                return True             # failed
        return False
