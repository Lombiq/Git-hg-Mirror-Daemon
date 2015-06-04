'''HTTP-based store for Kiln.'''

import urllib2

from mercurial import util, url as url_

import bfutil
import remotestore

class kilnstore(remotestore.remotestore):
    def __init__(self, ui, repo, remote):
        self.baseurl = bfutil.urljoin(remote.url(), 'bfile')
        try:
            # Mercurial >= 1.9
            self.baseurl, authinfo = util.url(self.baseurl).authinfo()
        except AttributeError:
            # Mercurial <= 1.8
            self.baseurl, authinfo = url_.getauthinfo(self.baseurl)
        self.opener = url_.opener(repo.ui, authinfo)
        super(kilnstore, self).__init__(ui, repo, remote.url())

    def _put(self, hash, fd):
        try:
            req = urllib2.Request(bfutil.urljoin(self.baseurl, hash), fd)
            resp = self.opener.open(req)
            return self._stat(hash) and 1 or 0
        except urllib2.HTTPError, e:
            return 1

    def _get(self, hash):
        req = urllib2.Request(bfutil.urljoin(self.baseurl, hash))
        return (None, self.opener.open(req))

    # '0' for OK, '1' for invalid checksum, '2' for missing
    def _stat(self, hash):
        try:
            req = urllib2.Request(bfutil.urljoin(self.baseurl, hash))
            req.add_header('SHA1-Request', hash)
            return int(hash != \
                self.opener.open(req).info().getheader('Content-SHA1'))
        except urllib2.HTTPError, e:
            if e.code == 404:
                return 2
            else:
                raise
