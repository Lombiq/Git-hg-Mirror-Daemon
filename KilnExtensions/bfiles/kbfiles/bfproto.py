import os
import shutil
import tempfile
import urllib2

from mercurial.i18n import _
from mercurial import error, httprepo, util, wireproto

import bfsetup
import bfutil

_heads_prefix = 'kbfiles\n'

def putbfile(repo, proto, sha):
    """putbfile puts a bfile into a repository's local cache and into the
    system cache."""
    f = None
    proto.redirect()
    try:
        try:
            f = tempfile.NamedTemporaryFile(mode='wb+', prefix='hg-putbfile-')
            proto.getfile(f)
            f.seek(0)
            if sha != bfutil.hexsha1(f):
                return wireproto.pushres(1)
            bfutil.copytocacheabsolute(repo, f.name, sha)
        except IOError:
            repo.ui.warn(
                _('error: could not put received data into bfile store'))
            return wireproto.pushres(1)
    finally:
        if f:
            f.close()

    return wireproto.pushres(0)

def getbfile(repo, proto, sha):
    """getbfile retrieves a bfile from the repository-local cache or system
    cache."""
    filename = bfutil.findfile(repo, sha)
    if not filename:
        raise util.Abort(_('requested bfile %s not present in cache') % sha)
    f = open(filename, 'rb')
    length = os.fstat(f.fileno())[6]
    # since we can't set an HTTP content-length header here, and mercurial core
    # provides no way to give the length of a streamres (and reading the entire
    # file into RAM would be ill-advised), we just send the length on the first
    # line of the response, like the ssh proto does for string responses.
    def generator():
        yield '%d\n' % length
        for chunk in f:
            yield chunk
    return wireproto.streamres(generator())

def statbfile(repo, proto, sha):
    """statbfile sends '2\n' if the bfile is missing, '1\n' if it has a
    mismatched checksum, or '0\n' if it is in good condition"""
    filename = bfutil.findfile(repo, sha)
    if not filename:
        return '2\n'
    fd = None
    try:
        fd = open(filename, 'rb')
        return bfutil.hexsha1(fd) == sha and '0\n' or '1\n'
    finally:
        if fd:
            fd.close()

def wirereposetup(ui, repo):
    class kbfileswirerepository(repo.__class__):
        def putbfile(self, sha, fd):
            # unfortunately, httprepository._callpush tries to convert its
            # input file-like into a bundle before sending it, so we can't use
            # it ...
            if issubclass(self.__class__, httprepo.httprepository):
                try:
                    return int(self._call('putbfile', data=fd, sha=sha,
                        headers={'content-type':'application/mercurial-0.1'}))
                except (ValueError, urllib2.HTTPError):
                    return 1
            # ... but we can't use sshrepository._call because the data=
            # argument won't get sent, and _callpush does exactly what we want
            # in this case: send the data straight through
            else:
                try:
                    ret, output = self._callpush("putbfile", fd, sha=sha)
                    if ret == "":
                        raise error.ResponseError(_('putbfile failed:'),
                                output)
                    return int(ret)
                except IOError:
                    return 1
                except ValueError:
                    raise error.ResponseError(
                        _('putbfile failed (unexpected response):'), ret)

        def getbfile(self, sha):
            stream = self._callstream("getbfile", sha=sha)
            length = stream.readline()
            try:
                length = int(length)
            except ValueError:
                self._abort(error.ResponseError(_("unexpected response:"), l))
            return (length, stream)

        def statbfile(self, sha):
            try:
                return int(self._call("statbfile", sha=sha))
            except (ValueError, urllib2.HTTPError):
                # if the server returns anything but an integer followed by a
                # newline, newline, it's not speaking our language; if we get
                # an HTTP error, we can't be sure the bfile is present; either
                # way, consider it missing
                return 2

        try:
            @wireproto.batchable
            def heads(self):
                f = wireproto.future()
                yield {}, f
                d = f.value
                if d[:len(_heads_prefix)] == _heads_prefix:
                    d = d[len(_heads_prefix):]
                try:
                    yield wireproto.decodelist(d[:-1])
                except ValueError:
                    self._abort(error.ResponseError(_("unexpected response:"), d))
        except AttributeError:
            # Mercurial < 1.9 has no @batchable; define a normal wirerepo heads
            # command
            def heads(self):
                d = self._call('heads')
                if d[:len(_heads_prefix)] == _heads_prefix:
                    d = d[len(_heads_prefix):]
                try:
                    return wireproto.decodelist(d[:-1])
                except ValueError:
                    self._abort(error.ResponseError(_("unexpected response:"), d))

    repo.__class__ = kbfileswirerepository

# wrap dispatch to check for and remove the kbfiles argument so commands with
# fixed argument lists don't complain
def dispatch(repo, proto, command):
    func, spec = wireproto.commands[command]
    args = proto.getargs(spec)

    # remove the kbfiles argument and ignore it: it still needs to be sent
    # to avoid breaking compatibility with older versions of the extension,
    # but it is unused in current versions
    if len(args) > 0 and isinstance(args[-1], dict):
        args[-1].pop('kbfiles', None)

    return func(repo, proto, *args)

# advertise the bfilestore=serve capability
def capabilities(repo, proto):
    return capabilities_orig(repo, proto) + ' bfilestore=serve'

def heads(repo, proto):
    if bfutil.iskbfilesrepo(repo):
        return _heads_prefix + wireproto.heads(repo, proto)
    return wireproto.heads(repo, proto)
