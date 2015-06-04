'''bfiles utility code: must not import other modules in this package.'''

import os
import errno
import inspect
import shutil
import stat
import hashlib

from mercurial import cmdutil, dirstate, httpconnection, match as match_, \
        url as url_, util
from mercurial.i18n import _

try:
    from mercurial import scmutil
except ImportError:
    pass

shortname = '.kbf'
longname = 'kilnbfiles'


# -- Portability wrappers ----------------------------------------------

if 'subrepos' in inspect.getargspec(dirstate.dirstate.status)[0]:
    # for Mercurial >= 1.5
    def dirstate_walk(dirstate, matcher, unknown=False, ignored=False):
        return dirstate.walk(matcher, [], unknown, ignored)
else:
    # for Mercurial <= 1.4
    def dirstate_walk(dirstate, matcher, unknown=False, ignored=False):
        return dirstate.walk(matcher, unknown, ignored)

def repo_add(repo, list):
    try:
        # Mercurial <= 1.5
        add = repo.add
    except AttributeError:
        # Mercurial >= 1.6
        add = repo[None].add
    return add(list)

def repo_remove(repo, list, unlink=False):
    try:
        # Mercurial <= 1.5
        remove = repo.remove
    except AttributeError:
        # Mercurial >= 1.6
        try:
            # Mercurial <= 1.8
            remove = repo[None].remove
        except AttributeError:
            # Mercurial >= 1.9
            def remove(list, unlink):
                wlock = repo.wlock()
                try:
                    if unlink:
                        for f in list:
                            try:
                                util.unlinkpath(repo.wjoin(f))
                            except OSError, inst:
                                if inst.errno != errno.ENOENT:
                                    raise
                    repo[None].forget(list)
                finally:
                    wlock.release()

    return remove(list, unlink=unlink)

def repo_forget(repo, list):
    try:
        # Mercurial <= 1.5
        forget = repo.forget
    except AttributeError:
        # Mercurial >= 1.6
        forget = repo[None].forget
    return forget(list)

def dirstate_normaldirty(dirstate, file):
    try:
        normaldirty = dirstate.normaldirty
    except AttributeError:
        # Mercurial >= 1.6: HAAAACK: I should not be using normaldirty()
        # (now called otherparent()), and dirstate in 1.6 prevents me
        # from doing so.  So reimplement it here until I figure out the
        # right fix.
        def normaldirty(f):
            dirstate._dirty = True
            dirstate._addpath(f)
            dirstate._map[f] = ('n', 0, -2, -1)
            if f in dirstate._copymap:
                del dirstate._copymap[f]
    normaldirty(file)

def findoutgoing(repo, remote, force):
    # First attempt is for Mercurial <= 1.5 second is for >= 1.6
    try:
        return repo.findoutgoing(remote)
    except AttributeError:
        from mercurial import discovery
        try:
            # Mercurial <= 1.8
            return discovery.findoutgoing(repo, remote, force=force)
        except AttributeError:
            # Mercurial >= 1.9
            common, _anyinc, _heads = discovery.findcommonincoming(repo,
                remote, force=force)
            return repo.changelog.findmissing(common)

# -- Private worker functions ------------------------------------------

if os.name == 'nt':
    from mercurial import win32
    try:
        linkfn = win32.oslink
    except:
        linkfn = win32.os_link
else:
    linkfn = os.link

def link(src, dest):
    try:
        linkfn(src, dest)
    except OSError:
        # If hardlinks fail fall back on copy
        shutil.copyfile(src, dest)
        os.chmod(dest, os.stat(src).st_mode)

def systemcachepath(ui, hash):
    path = ui.config(longname, 'systemcache', None)
    if path:
        path = os.path.join(path, hash)
    else:
        if os.name == 'nt':
            path = os.path.join(os.getenv('LOCALAPPDATA') or \
                os.getenv('APPDATA'), longname, hash)
        elif os.name == 'posix':
            path = os.path.join(os.getenv('HOME'), '.' + longname, hash)
        else:
            raise util.Abort(_('Unknown operating system: %s\n') % os.name)
    return path

def insystemcache(ui, hash):
    return os.path.exists(systemcachepath(ui, hash))

def findfile(repo, hash):
    if incache(repo, hash):
        repo.ui.note(_('Found %s in cache\n') % hash)
        return cachepath(repo, hash)
    if insystemcache(repo.ui, hash):
        repo.ui.note(_('Found %s in system cache\n') % hash)
        return systemcachepath(repo.ui, hash)
    return None

def openbfdirstate(ui, repo):
    '''
    Return a dirstate object that tracks big files: i.e. its root is the
    repo root, but it is saved in .hg/bfiles/dirstate.
    '''
    admin = repo.join(longname)
    try:
        # Mercurial >= 1.9
        opener = scmutil.opener(admin)
    except ImportError:
        # Mercurial <= 1.8
        opener = util.opener(admin)
    if hasattr(repo.dirstate, '_validate'):
        bfdirstate = dirstate.dirstate(opener, ui, repo.root,
            repo.dirstate._validate)
    else:
        bfdirstate = dirstate.dirstate(opener, ui, repo.root)

    # If the bfiles dirstate does not exist, populate and create it.  This
    # ensures that we create it on the first meaningful bfiles operation in
    # a new clone.  It also gives us an easy way to forcibly rebuild bfiles
    # state:
    #   rm .hg/bfiles/dirstate && hg bfstatus
    # Or even, if things are really messed up:
    #   rm -rf .hg/bfiles && hg bfstatus
    # (although that can lose data, e.g. pending big file revisions in
    # .hg/bfiles/{pending,committed}).
    if not os.path.exists(os.path.join(admin, 'dirstate')):
        util.makedirs(admin)
        matcher = getstandinmatcher(repo)
        for standin in dirstate_walk(repo.dirstate, matcher):
            bigfile = splitstandin(standin)
            hash = readstandin(repo, standin)
            try:
                curhash = hashfile(bigfile)
            except IOError, err:
                if err.errno == errno.ENOENT:
                    dirstate_normaldirty(bfdirstate, bigfile)
                else:
                    raise
            else:
                if curhash == hash:
                    bfdirstate.normal(unixpath(bigfile))
                else:
                    dirstate_normaldirty(bfdirstate, bigfile)

        bfdirstate.write()

    return bfdirstate

def bfdirstate_status(bfdirstate, repo, rev):
    wlock = repo.wlock()
    try:
        match = match_.always(repo.root, repo.getcwd())
        s = bfdirstate.status(match, [], False, False, False)
        unsure, modified, added, removed, missing, unknown, ignored, clean = s
        for bfile in unsure:
            if repo[rev][standin(bfile)].data().strip() != \
                    hashfile(repo.wjoin(bfile)):
                modified.append(bfile)
            else:
                clean.append(bfile)
                bfdirstate.normal(unixpath(bfile))
        bfdirstate.write()
    finally:
        wlock.release()
    return (modified, added, removed, missing, unknown, ignored, clean)

def listbfiles(repo, rev=None, matcher=None):
    '''list big files in the working copy or specified changeset'''

    if matcher is None:
        matcher = getstandinmatcher(repo)

    bfiles = []
    if rev is not None:
        cctx = repo[rev]
        for standin in cctx.walk(matcher):
            filename = splitstandin(standin)
            bfiles.append(filename)
    else:
        for standin in sorted(dirstate_walk(repo.dirstate, matcher)):
            filename = splitstandin(standin)
            bfiles.append(filename)
    return bfiles

def incache(repo, hash):
    return os.path.exists(cachepath(repo, hash))

def createdir(dir):
    if not os.path.exists(dir):
        os.makedirs(dir)

def cachepath(repo, hash):
    return repo.join(os.path.join(longname, hash))

def copytocache(repo, rev, file, uploaded=False):
    hash = readstandin(repo, standin(file))
    if incache(repo, hash):
        return
    copytocacheabsolute(repo, repo.wjoin(file), hash)

def copytocacheabsolute(repo, file, hash):
    createdir(os.path.dirname(cachepath(repo, hash)))
    if insystemcache(repo.ui, hash):
        link(systemcachepath(repo.ui, hash), cachepath(repo, hash))
    else:
        shutil.copyfile(file, cachepath(repo, hash))
        os.chmod(cachepath(repo, hash), os.stat(file).st_mode)
        createdir(os.path.dirname(systemcachepath(repo.ui, hash)))
        link(cachepath(repo, hash), systemcachepath(repo.ui, hash))

def getstandinmatcher(repo, pats=[], opts={}):
    '''Return a match object that applies pats to <repo>/.kbf.'''
    standindir = repo.pathto(shortname)
    if pats:
        # patterns supplied: search .hgbfiles relative to current dir
        cwd = repo.getcwd()
        if os.path.isabs(cwd):
            # cwd is an absolute path for hg -R <reponame>
            # work relative to the repository root in this case
            cwd = ''
        pats = [os.path.join(standindir, cwd, pat) for pat in pats]
    elif os.path.isdir(standindir):
        # no patterns: relative to repo root
        pats = [standindir]
    else:
        # no patterns and no .hgbfiles dir: return matcher that matches nothing
        match = match_.match(repo.root, None, [], exact=True)
        match.matchfn = lambda f: False
        return match
    return getmatcher(repo, pats, opts, showbad=False)

def getmatcher(repo, pats=[], opts={}, showbad=True):
    '''Wrapper around scmutil.match() that adds showbad: if false, neuter
    the match object\'s bad() method so it does not print any warnings
    about missing files or directories.'''
    try:
        # Mercurial >= 1.9
        match = scmutil.match(repo[None], pats, opts)
    except ImportError:
        # Mercurial <= 1.8
        match = cmdutil.match(repo, pats, opts)

    if not showbad:
        match.bad = lambda f, msg: None
    return match

def composestandinmatcher(repo, rmatcher):
    '''Return a matcher that accepts standins corresponding to the files
    accepted by rmatcher. Pass the list of files in the matcher as the
    paths specified by the user.'''
    smatcher = getstandinmatcher(repo, rmatcher.files())
    isstandin = smatcher.matchfn
    def composed_matchfn(f):
        return isstandin(f) and rmatcher.matchfn(splitstandin(f))
    smatcher.matchfn = composed_matchfn

    return smatcher

def standin(filename):
    '''Return the repo-relative path to the standin for the specified big
    file.'''
    # Notes:
    # 1) Most callers want an absolute path, but _create_standin() needs
    #    it repo-relative so bfadd() can pass it to repo_add().  So leave
    #    it up to the caller to use repo.wjoin() to get an absolute path.
    # 2) Join with '/' because that's what dirstate always uses, even on
    #    Windows. Change existing separator to '/' first in case we are
    #    passed filenames from an external source (like the command line).
    return shortname + '/' + filename.replace(os.sep, '/')

def isstandin(filename):
    '''Return true if filename is a big file standin.  filename must
    be in Mercurial\'s internal form (slash-separated).'''
    return filename.startswith(shortname + '/')

def splitstandin(filename):
    # Split on / because that's what dirstate always uses, even on Windows.
    # Change local separator to / first just in case we are passed filenames
    # from an external source (like the command line).
    bits = filename.replace(os.sep, '/').split('/', 1)
    if len(bits) == 2 and bits[0] == shortname:
        return bits[1]
    else:
        return None

def updatestandin(repo, standin):
    file = repo.wjoin(splitstandin(standin))
    if os.path.exists(file):
        hash = hashfile(file)
        executable = getexecutable(file)
        writestandin(repo, standin, hash, executable)

def readstandin(repo, standin):
    '''read hex hash from <repo.root>/<standin>'''
    return readhash(repo.wjoin(standin))

def writestandin(repo, standin, hash, executable):
    '''write hhash to <repo.root>/<standin>'''
    writehash(hash, repo.wjoin(standin), executable)

def copyandhash(instream, outfile):
    '''Read bytes from instream (iterable) and write them to outfile,
    computing the SHA-1 hash of the data along the way.  Close outfile
    when done and return the binary hash.'''
    hasher = util.sha1('')
    for data in instream:
        hasher.update(data)
        outfile.write(data)

    # Blecch: closing a file that somebody else opened is rude and
    # wrong.  But it's so darn convenient and practical!  After all,
    # outfile was opened just to copy and hash.
    outfile.close()

    return hasher.digest()

def hashrepofile(repo, file):
    return hashfile(repo.wjoin(file))

def hashfile(file):
    if not os.path.exists(file):
        return ''
    hasher = util.sha1('')
    fd = open(file, 'rb')
    for data in blockstream(fd):
        hasher.update(data)
    fd.close()
    return hasher.hexdigest()

class limitreader(object):
    def __init__(self, f, limit):
        self.f = f
        self.limit = limit

    def read(self, length):
        if self.limit == 0:
            return ''
        length = length > self.limit and self.limit or length
        self.limit -= length
        return self.f.read(length)

    def close(self):
        pass

def blockstream(infile, blocksize=128 * 1024):
    """Generator that yields blocks of data from infile and closes infile."""
    while True:
        data = infile.read(blocksize)
        if not data:
            break
        yield data
    # Same blecch as above.
    infile.close()

def readhash(filename):
    rfile = open(filename, 'rb')
    hash = rfile.read(40)
    rfile.close()
    if len(hash) < 40:
        raise util.Abort(_('bad hash in \'%s\' (only %d bytes long)')
                         % (filename, len(hash)))
    return hash

def writehash(hash, filename, executable):
    util.makedirs(os.path.dirname(filename))
    if os.path.exists(filename):
        os.unlink(filename)
    if os.name == 'posix':
        # Yuck: on Unix, go through open(2) to ensure that the caller's mode is
        # filtered by umask() in the kernel, where it's supposed to be done.
        wfile = os.fdopen(os.open(filename, os.O_WRONLY|os.O_CREAT,
            getmode(executable)), 'wb')
    else:
        # But on Windows, use open() directly, since passing mode='wb' to
        # os.fdopen() does not work.  (Python bug?)
        wfile = open(filename, 'wb')

    try:
        wfile.write(hash)
        wfile.write('\n')
    finally:
        wfile.close()

def getexecutable(filename):
    mode = os.stat(filename).st_mode
    return (mode & stat.S_IXUSR) and (mode & stat.S_IXGRP) and (mode & \
        stat.S_IXOTH)

def getmode(executable):
    if executable:
        return 0755
    else:
        return 0644

def urljoin(first, second, *arg):
    def join(left, right):
        if not left.endswith('/'):
            left += '/'
        if right.startswith('/'):
            right = right[1:]
        return left + right

    url = join(first, second)
    for a in arg:
        url = join(url, a)
    return url

def hexsha1(data):
    """hexsha1 returns the hex-encoded sha1 sum of the data in the file-like
    object data"""
    h = hashlib.sha1()
    for chunk in util.filechunkiter(data):
        h.update(chunk)
    return h.hexdigest()

def httpsendfile(ui, filename):
    try:
        # Mercurial >= 1.9
        sendfile = httpconnection.httpsendfile(ui, filename, 'rb')
        if getattr(sendfile, '__len__', None) is None:
            # Mercurial 1.9.3 removes httpsendfile's __len__. Hack it back in.
            setattr(sendfile.__class__, '__len__', lambda self: self.length)
        return sendfile
    except ImportError:
        if 'ui' in inspect.getargspec(url_.httpsendfile.__init__)[0]:
            # Mercurial == 1.8
            return url_.httpsendfile(ui, filename, 'rb')
        else:
            # Mercurial <= 1.7
            return url_.httpsendfile(filename, 'rb')

# Convert a path to a unix style path. This is used to give a
# canonical path to the bfdirstate.
def unixpath(path):
    return os.path.normpath(path).replace(os.sep, '/')

def iskbfilesrepo(repo):
    return 'kbfiles' in repo.requirements and any_('.kbf/' in f[0] for f in
        repo.store.datafiles())

def any_(gen):
    for x in gen:
        if x:
            return True
    return False
