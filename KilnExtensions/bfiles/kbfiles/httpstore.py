'''HTTP-based store.'''

import inspect, urlparse, urllib2

from mercurial import util, hg, url as url_
from mercurial.i18n import _

try:
    from mercurial import httpconnection
except ImportError:
    pass

import bfutil, basestore

class httpstore(basestore.basestore):
    """A store accessed via HTTP"""
    def __init__(self, ui, repo, url):
        store_type = None
        try:
            remoterepo = hg.peer(repo, dict([]), url)
            store_type = remoterepo.capable('bfilestore')
        except:
            pass
        if store_type == 'serve':
            self.proto = hgservestoreproto(remoterepo, url)
        elif store_type == 'kiln':
            self.proto = kilnstoreproto(ui, url)
        else:
            ui.note(_('bfilestore capability not found; assuming %s is a Kiln repo') % url)
            self.proto = kilnstoreproto(ui, url)
        self.url = self.proto.url
        self.rawurl, authinfo = urlparse.urlsplit(self.url)[1:3]
        super(httpstore, self).__init__(ui, repo, url)

    def put(self, source, hash):
        self.sendfile(source, hash)
        if not self._verify(hash):
            raise util.Abort(_('could not put %s to remote store') % source)
        self.ui.debug('put %s to remote store\n' % source)

    def exists(self, hash):
        return self._verify(hash)

    def sendfile(self, filename, hash):
        if self._verify(hash):
            return

        self.ui.debug('httpstore.sendfile(%s, %s)\n' % (filename, hash))
        Ffd = None
        try:
            try:
                # Mercurial >= 1.9
                fd = httpconnection.httpsendfile(self.ui, filename, 'rb')
            except ImportError:
                if 'ui' in inspect.getargspec(url_.httpsendfile.__init__)[0]:
                    # Mercurial == 1.8
                    fd = url_.httpsendfile(self.ui, filename, 'rb')
                else:
                    # Mercurial <= 1.7
                    fd = url_.httpsendfile(filename, 'rb')
            try:
                url = self.proto.put(hash, fd)
            except urllib2.HTTPError, e:
                raise util.Abort(_('unable to POST: %s\n') % e.msg)
        except Exception, e:
            raise util.Abort(_('%s') % e)
        finally:
            if fd: fd.close()

    def _getfile(self, tmpfile, filename, hash):
        stat = self.proto.stat(hash)
        if stat:
            raise util.Abort(_('bfile %s is %s') %
                                      (hash, 'invalid' if stat == 1 else 'missing'))
        try:
            infile = self.proto.get(hash)
        except urllib2.HTTPError, err:
            detail = _("HTTP error: %s %s") % (err.code, err.msg)
            raise basestore.StoreError(filename, hash, self.url, detail)
        except urllib2.URLError, err:
            # This usually indicates a connection problem, so don't
            # keep trying with the other files... they will probably
            # all fail too.
            reason = err[0][1]      # assumes err[0] is a socket.error
            raise util.Abort('%s: %s' % (self.url, reason))
        return bfutil.copyandhash(bfutil.blockstream(infile), tmpfile)

    def _verify(self, hash):
        return not self.proto.stat(hash)

    def _verifyfile(self, cctx, cset, contents, standin, verified):
        filename = bfutil.splitstandin(standin)
        if not filename:
            return False
        fctx = cctx[standin]
        key = (filename, fctx.filenode())
        if key in verified:
            return False

        expect_hash = fctx.data()[0:40]
        verified.add(key)

        stat = self.proto.stat(hash)
        if not stat:
            return False
        elif stat == 1:
            self.ui.warn(
                _('changeset %s: %s: contents differ\n (%s)\n')
                % (cset, filename, store_path))
            return True # failed
        elif stat == 2:
            self.ui.warn(
                _('changeset %s: %s missing\n (%s)\n')
                % (cset, filename, store_path))
            return True # failed
        else:
            raise util.Abort(_('check failed, unexpected response'
                               'statbfile: %d') % stat)

class kilnstoreproto(object):
    def __init__(self, ui, url):
        self.url = bfutil.urljoin(url, "bfile")
        try:
            # Mercurial >= 1.9
            baseurl, authinfo = util.url(self.url).authinfo()
        except AttributeError:
            # Mercurial <= 1.8
            baseurl, authinfo = url_.getauthinfo(self.url)
        self.opener = url_.opener(ui, authinfo)
    def put(self, hash, fd):
        req = urllib2.Request(bfutil.urljoin(self.url, hash), fd)
        return self.opener.open(req)
    def get(self, hash):
        req = urllib2.Request(bfutil.urljoin(self.url, hash))
        req.add_header('SHA1-Request', hash)
        return self.opener.open(req)
    # '0' for OK, '1' for invalid checksum, '2' for missing
    def stat(self, hash):
        try:
            return 0 if hash == bfutil.hexsha1(self.get(hash)) else 1
        except urllib2.HTTPError, e:
            if e.code == 404:
                return 2
            else:
                raise

class hgservestoreproto(object):
    def __init__(self, repo, url):
        self.repo = repo
        self.url = url
    def put(self, hash, fd):
        return self.repo.putbfile(hash, fd)
    def get(self, hash):
        return self.repo.getbfile(hash)
    def stat(self, hash):
        return self.repo.statbfile(hash)
