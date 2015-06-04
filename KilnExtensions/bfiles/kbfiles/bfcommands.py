'''High-level command functions: bfadd() et. al, plus the cmdtable.'''

import os
import shutil

from mercurial import util, match as match_, hg, node, context, error
from mercurial.i18n import _

import bfutil
import basestore

# -- Commands ----------------------------------------------------------

def bfconvert(ui, src, dest, *pats, **opts):
    '''Convert a repository to a repository using bfiles

    Convert source repository creating an identical
    repository, except that all files that match the
    patterns given, or are over a given size will
    be added as bfiles. The size of a file is the size of the
    first version of the file. After running this command you
    will need to set the store then run bfput on the new
    repository to upload the bfiles to the central store.
    '''

    if opts['tonormal']:
        tobfile = False
    else:
        tobfile = True
        size = opts['size']
        if not size:
            size = ui.config(bfutil.longname, 'size', default=None)
            try:
                size = int(size)
            except ValueError:
                raise util.Abort(_('bfiles.size must be integer, was %s\n') % \
                    size)
            except TypeError:
                raise util.Abort(_('size must be specified'))

    try:
        rsrc = hg.repository(ui, src)
        if not rsrc.local():
            raise util.Abort(_('%s is not a local Mercurial repo') % src)
    except error.RepoError, err:
        ui.traceback()
        raise util.Abort(err.args[0])
    if os.path.exists(dest):
        if not os.path.isdir(dest):
            raise util.Abort(_('destination %s already exists') % dest)
        elif os.listdir(dest):
            raise util.Abort(_('destination %s is not empty') % dest)
    try:
        ui.status(_('initializing destination %s\n') % dest)
        rdst = hg.repository(ui, dest, create=True)
        if not rdst.local():
            raise util.Abort(_('%s is not a local Mercurial repo') % dest)
    except error.RepoError:
        ui.traceback()
        raise util.Abort(_('%s is not a repo') % dest)

    try:
        # Lock destination to prevent modification while it is converted to.
        # Don't need to lock src because we are just reading from its history
        # which can't change.
        dst_lock = rdst.lock()

        # Get a list of all changesets in the source.  The easy way to do this
        # is to simply walk the changelog, using changelog.nodesbewteen().
        # Take a look at mercurial/revlog.py:639 for more details.
        # Use a generator instead of a list to decrease memory usage
        ctxs = (rsrc[ctx] for ctx in rsrc.changelog.nodesbetween(None,
            rsrc.heads())[0])
        revmap = {node.nullid: node.nullid}
        if tobfile:
            bfiles = set()
            normalfiles = set()
            if not pats:
                pats = ui.config(bfutil.longname, 'patterns', default=())
                if pats:
                    pats = pats.split(' ')
            if pats:
                matcher = match_.match(rsrc.root, '', list(pats))
            else:
                matcher = None

            bfiletohash = {}
            for ctx in ctxs:
                ui.progress(_('converting revisions'), ctx.rev(),
                    unit=_('revision'), total=rsrc['tip'].rev())
                _bfconvert_addchangeset(rsrc, rdst, ctx, revmap,
                    bfiles, normalfiles, matcher, size, bfiletohash)
            ui.progress(_('converting revisions'), None)

            if os.path.exists(rdst.wjoin(bfutil.shortname)):
                shutil.rmtree(rdst.wjoin(bfutil.shortname))

            for f in bfiletohash.keys():
                if os.path.isfile(rdst.wjoin(f)):
                    os.unlink(rdst.wjoin(f))
                try:
                    os.removedirs(os.path.dirname(rdst.wjoin(f)))
                except:
                    pass

        else:
            for ctx in ctxs:
                ui.progress(_('converting revisions'), ctx.rev(),
                    unit=_('revision'), total=rsrc['tip'].rev())
                _addchangeset(ui, rsrc, rdst, ctx, revmap)

            ui.progress(_('converting revisions'), None)
    except:
        # we failed, remove the new directory
        shutil.rmtree(rdst.root)
        raise
    finally:
        dst_lock.release()

def _addchangeset(ui, rsrc, rdst, ctx, revmap):
 # Convert src parents to dst parents
    parents = []
    for p in ctx.parents():
        parents.append(revmap[p.node()])
    while len(parents) < 2:
        parents.append(node.nullid)

    # Generate list of changed files
    files = set(ctx.files())
    if node.nullid not in parents:
        mc = ctx.manifest()
        mp1 = ctx.parents()[0].manifest()
        mp2 = ctx.parents()[1].manifest()
        for f in mp1:
            if f not in mc:
                files.add(f)
        for f in mp2:
            if f not in mc:
                files.add(f)
        for f in mc:
            if mc[f] != mp1.get(f, None) or mc[f] != mp2.get(f, None):
                files.add(f)

    def getfilectx(repo, memctx, f):
        if bfutil.standin(f) in files:
            # if the file isn't in the manifest then it was removed
            # or renamed, raise IOError to indicate this
            try:
                fctx = ctx.filectx(bfutil.standin(f))
            except error.LookupError:
                raise IOError()
            renamed = fctx.renamed()
            if renamed:
                renamed = bfutil.splitstandin(renamed[0])

            hash = fctx.data().strip()
            path = bfutil.findfile(rsrc, hash)
            ### TODO: What if the file is not cached?
            data = ''
            fd = None
            try:
                fd = open(path, 'rb')
                data = fd.read()
            finally:
                if fd: fd.close()
            return context.memfilectx(f, data, 'l' in fctx.flags(),
                                      'x' in fctx.flags(), renamed)
        else:
            try:
                fctx = ctx.filectx(f)
            except error.LookupError:
                raise IOError()
            renamed = fctx.renamed()
            if renamed:
                renamed = renamed[0]
            data = fctx.data()
            if f == '.hgtags':
                newdata = []
                for line in data.splitlines():
                    id, name = line.split(' ', 1)
                    newdata.append('%s %s\n' % (node.hex(revmap[node.bin(id)]),
                        name))
                data = ''.join(newdata)
            return context.memfilectx(f, data, 'l' in fctx.flags(),
                                      'x' in fctx.flags(), renamed)

    dstfiles = []
    for file in files:
        if bfutil.isstandin(file):
            dstfiles.append(bfutil.splitstandin(file))
        else:
            dstfiles.append(file)
    # Commit
    mctx = context.memctx(rdst, parents, ctx.description(), dstfiles,
                          getfilectx, ctx.user(), ctx.date(), ctx.extra())
    ret = rdst.commitctx(mctx)
    rdst.dirstate.setparents(ret)
    revmap[ctx.node()] = rdst.changelog.tip()

def _bfconvert_addchangeset(rsrc, rdst, ctx, revmap, bfiles, normalfiles,
        matcher, size, bfiletohash):
    # Convert src parents to dst parents
    parents = []
    for p in ctx.parents():
        parents.append(revmap[p.node()])
    while len(parents) < 2:
        parents.append(node.nullid)

    # Generate list of changed files
    files = set(ctx.files())
    if node.nullid not in parents:
        mc = ctx.manifest()
        mp1 = ctx.parents()[0].manifest()
        mp2 = ctx.parents()[1].manifest()
        for f in mp1:
            if f not in mc:
                files.add(f)
        for f in mp2:
            if f not in mc:
                files.add(f)
        for f in mc:
            if mc[f] != mp1.get(f, None) or mc[f] != mp2.get(f, None):
                files.add(f)

    dstfiles = []
    for f in files:
        if f not in bfiles and f not in normalfiles:
            isbfile = _isbfile(f, ctx, matcher, size)
            # If this file was renamed or copied then copy
            # the bfileness of its predecessor
            if f in ctx.manifest():
                fctx = ctx.filectx(f)
                renamed = fctx.renamed()
                renamedbfile = renamed and renamed[0] in bfiles
                isbfile |= renamedbfile
                if 'l' in fctx.flags():
                    if renamedbfile:
                        raise util.Abort(
                            _('Renamed/copied bfile %s becomes symlink') % f)
                    isbfile = False
            if isbfile:
                bfiles.add(f)
            else:
                normalfiles.add(f)

        if f in bfiles:
            dstfiles.append(bfutil.standin(f))
            # bfile in manifest if it has not been removed/renamed
            if f in ctx.manifest():
                if 'l' in ctx.filectx(f).flags():
                    if renamed and renamed[0] in bfiles:
                        raise util.Abort(_('bfile %s becomes symlink') % f)

                # bfile was modified, update standins
                fullpath = rdst.wjoin(f)
                bfutil.createdir(os.path.dirname(fullpath))
                m = util.sha1('')
                m.update(ctx[f].data())
                hash = m.hexdigest()
                if f not in bfiletohash or bfiletohash[f] != hash:
                    try:
                        fd = open(fullpath, 'wb')
                        fd.write(ctx[f].data())
                    finally:
                        if fd:
                            fd.close()
                    executable = 'x' in ctx[f].flags()
                    os.chmod(fullpath, bfutil.getmode(executable))
                    bfutil.writestandin(rdst, bfutil.standin(f), hash,
                        executable)
                    bfiletohash[f] = hash
        else:
            # normal file
            dstfiles.append(f)

    def getfilectx(repo, memctx, f):
        if bfutil.isstandin(f):
            # if the file isn't in the manifest then it was removed
            # or renamed, raise IOError to indicate this
            srcfname = bfutil.splitstandin(f)
            try:
                fctx = ctx.filectx(srcfname)
            except error.LookupError:
                raise IOError()
            renamed = fctx.renamed()
            if renamed:
                # standin is always a bfile because bfileness
                # doesn't change after rename or copy
                renamed = bfutil.standin(renamed[0])

            return context.memfilectx(f, bfiletohash[srcfname], 'l' in
                fctx.flags(),
                                      'x' in fctx.flags(), renamed)
        else:
            try:
                fctx = ctx.filectx(f)
            except error.LookupError:
                raise IOError()
            renamed = fctx.renamed()
            if renamed:
                renamed = renamed[0]

            data = fctx.data()
            if f == '.hgtags':
                newdata = []
                for line in data.splitlines():
                    id, name = line.split(' ', 1)
                    newdata.append('%s %s\n' % (node.hex(revmap[node.bin(id)]),
                        name))
                data = ''.join(newdata)
            return context.memfilectx(f, data, 'l' in fctx.flags(),
                                      'x' in fctx.flags(), renamed)

    # Commit
    mctx = context.memctx(rdst, parents, ctx.description(), dstfiles,
                          getfilectx, ctx.user(), ctx.date(), ctx.extra())
    ret = rdst.commitctx(mctx)
    rdst.dirstate.setparents(ret)
    revmap[ctx.node()] = rdst.changelog.tip()

def _isbfile(file, ctx, matcher, size):
    '''
    A file is a bfile if it matches a pattern or is over
    the given size.
    '''
    # Never store hgtags or hgignore as bfiles
    if file == '.hgtags' or file == '.hgignore' or file == '.hgsigs':
        return False
    if matcher and matcher(file):
        return True
    try:
        return ctx.filectx(file).size() >= size * 1024 * 1024
    except error.LookupError:
        return False

def uploadbfiles(ui, rsrc, rdst, files):
    '''upload big files to the central store'''

    # Don't upload locally. All bfiles are in the system wide cache
    # so the other repo can just get them from there.
    if not files or rdst.local():
        return

    store = basestore._openstore(rsrc, rdst, put=True)

    at = 0
    for hash in files:
        ui.progress(_('uploading bfiles'), at, unit='bfile', total=len(files))
        if store.exists(hash):
            at += 1
            continue
        source = bfutil.findfile(rsrc, hash)
        if not source:
            raise util.Abort(_('Missing bfile %s needs to be uploaded') % hash)
        # XXX check for errors here
        store.put(source, hash)
        at += 1
    ui.progress('uploading bfiles', None)

def verifybfiles(ui, repo, all=False, contents=False):
    '''Verify that every big file revision in the current changeset
    exists in the central store.  With --contents, also verify that
    the contents of each big file revision are correct (SHA-1 hash
    matches the revision ID).  With --all, check every changeset in
    this repository.'''
    if all:
        # Pass a list to the function rather than an iterator because we know a
        # list will work.
        revs = range(len(repo))
    else:
        revs = ['.']

    store = basestore._openstore(repo)
    return store.verify(revs, contents=contents)

def revertbfiles(ui, repo, filelist=None):
    wlock = repo.wlock()
    try:
        bfdirstate = bfutil.openbfdirstate(ui, repo)
        s = bfdirstate.status(match_.always(repo.root, repo.getcwd()), [],
            False, False, False)
        unsure, modified, added, removed, missing, unknown, ignored, clean = s

        bfiles = bfutil.listbfiles(repo)
        toget = []
        at = 0
        updated = 0
        for bfile in bfiles:
            if filelist is None or bfile in filelist:
                if not os.path.exists(repo.wjoin(bfutil.standin(bfile))):
                    bfdirstate.remove(bfile)
                    continue
                if os.path.exists(repo.wjoin(bfutil.standin(os.path.join(bfile\
                    + '.orig')))):
                    shutil.copyfile(repo.wjoin(bfile), repo.wjoin(bfile + \
                        '.orig'))
                at += 1
                expectedhash = repo[None][bfutil.standin(bfile)].data().strip()
                mode = os.stat(repo.wjoin(bfutil.standin(bfile))).st_mode
                if not os.path.exists(repo.wjoin(bfile)) or expectedhash != \
                        bfutil.hashfile(repo.wjoin(bfile)):
                    path = bfutil.findfile(repo, expectedhash)
                    if path is None:
                        toget.append((bfile, expectedhash))
                    else:
                        util.makedirs(os.path.dirname(repo.wjoin(bfile)))
                        shutil.copy(path, repo.wjoin(bfile))
                        os.chmod(repo.wjoin(bfile), mode)
                        updated += 1
                        if bfutil.standin(bfile) not in repo['.']:
                            bfdirstate.add(bfutil.unixpath(bfile))
                        elif expectedhash == repo['.'][bfutil.standin(bfile)] \
                                .data().strip():
                            bfdirstate.normal(bfutil.unixpath(bfile))
                        else:
                            bfutil.dirstate_normaldirty(bfdirstate,
                                bfutil.unixpath(bfile))
                elif os.path.exists(repo.wjoin(bfile)) and mode != \
                        os.stat(repo.wjoin(bfile)).st_mode:
                    os.chmod(repo.wjoin(bfile), mode)
                    updated += 1
                    if bfutil.standin(bfile) not in repo['.']:
                        bfdirstate.add(bfutil.unixpath(bfile))
                    elif expectedhash == \
                            repo['.'][bfutil.standin(bfile)].data().strip():
                        bfdirstate.normal(bfutil.unixpath(bfile))
                    else:
                        bfutil.dirstate_normaldirty(bfdirstate,
                            bfutil.unixpath(bfile))

        if toget:
            store = basestore._openstore(repo)
            success, missing = store.get(toget)
        else:
            success, missing = [], []

        for (filename, hash) in success:
            mode = os.stat(repo.wjoin(bfutil.standin(filename))).st_mode
            os.chmod(repo.wjoin(filename), mode)
            updated += 1
            if bfutil.standin(filename) not in repo['.']:
                bfdirstate.add(bfutil.unixpath(filename))
            elif hash == repo['.'][bfutil.standin(filename)].data().strip():
                bfdirstate.normal(bfutil.unixpath(filename))
            else:
                bfutil.dirstate_normaldirty(bfdirstate,
                    bfutil.unixpath(filename))

        removed = 0
        for bfile in bfdirstate:
            if filelist is None or bfile in filelist:
                if not os.path.exists(repo.wjoin(bfutil.standin(bfile))):
                    if os.path.exists(repo.wjoin(bfile)):
                        os.unlink(repo.wjoin(bfile))
                        removed += 1
                        if bfutil.standin(bfile) in repo['.']:
                            bfdirstate.remove(bfutil.unixpath(bfile))
                        else:
                            bfdirstate.forget(bfutil.unixpath(bfile))
                else:
                    state = repo.dirstate[bfutil.standin(bfile)]
                    if state == 'n':
                        bfdirstate.normal(bfile)
                    elif state == 'r':
                        bfdirstate.remove(bfile)
                    elif state == 'a':
                        bfdirstate.add(bfile)
                    elif state == '?':
                        try:
                            # Mercurial >= 1.9
                            bfdirstate.drop(bfile)
                        except AttributeError:
                            # Mercurial <= 1.8
                            bfdirstate.forget(bfile)
        bfdirstate.write()
    finally:
        wlock.release()

def updatebfiles(ui, repo):
    wlock = repo.wlock()
    try:
        bfdirstate = bfutil.openbfdirstate(ui, repo)
        s = bfdirstate.status(match_.always(repo.root, repo.getcwd()), [],
            False, False, False)
        unsure, modified, added, removed, missing, unknown, ignored, clean = s

        bfiles = bfutil.listbfiles(repo)
        toget = []
        at = 0
        updated = 0
        removed = 0
        printed = False
        if bfiles:
            ui.status(_('getting changed bfiles\n'))
            printed = True

        for bfile in bfiles:
            at += 1
            if os.path.exists(repo.wjoin(bfile)) and not \
                    os.path.exists(repo.wjoin(bfutil.standin(bfile))):
                os.unlink(repo.wjoin(bfile))
                removed += 1
                bfdirstate.forget(bfutil.unixpath(bfile))
                continue
            expectedhash = repo[None][bfutil.standin(bfile)].data().strip()
            mode = os.stat(repo.wjoin(bfutil.standin(bfile))).st_mode
            if not os.path.exists(repo.wjoin(bfile)) or expectedhash != \
                    bfutil.hashfile(repo.wjoin(bfile)):
                path = bfutil.findfile(repo, expectedhash)
                if not path:
                    toget.append((bfile, expectedhash))
                else:
                    util.makedirs(os.path.dirname(repo.wjoin(bfile)))
                    shutil.copy(path,  repo.wjoin(bfile))
                    os.chmod(repo.wjoin(bfile), mode)
                    updated += 1
                    bfdirstate.normal(bfutil.unixpath(bfile))
            elif os.path.exists(repo.wjoin(bfile)) and mode != \
                    os.stat(repo.wjoin(bfile)).st_mode:
                os.chmod(repo.wjoin(bfile), mode)
                updated += 1
                bfdirstate.normal(bfutil.unixpath(bfile))

        if toget:
            store = basestore._openstore(repo)
            (success, missing) = store.get(toget)
        else:
            success, missing = [],[]

        for (filename, hash) in success:
            mode = os.stat(repo.wjoin(bfutil.standin(filename))).st_mode
            os.chmod(repo.wjoin(filename), mode)
            updated += 1
            bfdirstate.normal(bfutil.unixpath(filename))

        for bfile in bfdirstate:
            if bfile not in bfiles:
                if os.path.exists(repo.wjoin(bfile)):
                    if not printed:
                        ui.status(_('getting changed bfiles\n'))
                        printed = True
                    os.unlink(repo.wjoin(bfile))
                    removed += 1
                    path = bfutil.unixpath(bfile)
                    try:
                        # Mercurial >= 1.9
                        bfdirstate.drop(path)
                    except AttributeError:
                        # Mercurial <= 1.8
                        bfdirstate.forget(path)

        bfdirstate.write()
        if printed:
            ui.status(_('%d big files updated, %d removed\n') % (updated,
                removed))
    finally:
        wlock.release()

# -- hg commands declarations ------------------------------------------------


cmdtable = {
    'kbfconvert': (bfconvert,
                  [('s', 'size', 0, 'All files over this size (in megabytes) '
                  'will be considered bfiles. This can also be specified in '
                  'your hgrc as [bfiles].size.'),
                  ('','tonormal',False,
                      'Convert from a bfiles repo to a normal repo')],
                  _('hg kbfconvert SOURCE DEST [FILE ...]')),
    }
