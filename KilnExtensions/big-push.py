# Copyright (C) 2009-2013 Fog Creek Software. All rights reserved.
#
# This program is free software; you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation; either version 2 of the License, or
# (at your option) any later version.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with this program; if not, write to the Free Software
# Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA 02111-1307, USA.

'''automatically push large repositories in chunks'''

from mercurial import cmdutil, commands, hg, extensions
from mercurial.i18n import _

max_push_size = 1000


def findoutgoing(repo, other):
    try:
        # Mercurial 1.6 through 1.8
        from mercurial import discovery
        return discovery.findoutgoing(repo, other, force=False)
    except AttributeError:
        # Mercurial 1.9 and higher
        common, _anyinc, _heads = discovery.findcommonincoming(repo, other, force=False)
        return repo.changelog.findmissing(common)
    except ImportError:
        # Mercurial 1.5 and lower
        return repo.findoutgoing(other, force=False)


def prepush(repo, other, force, revs):
    try:
        from mercurial import discovery
        try:
            # Mercurial 1.6 through 2.0
            return discovery.prepush(repo, other, force, revs, False)
        except AttributeError:
            # Mercurial 2.1 and higher
            fci = discovery.findcommonincoming(repo, other, force=force)
            outgoing = discovery.findcommonoutgoing(repo, other, onlyheads=revs, commoninc=fci, force=force)
            if not force:
                discovery.checkheads(repo, other, outgoing, fci[2], False)
            return [True]
    except ImportError:
        # Mercurial 1.5 and lower
        return repo.prepush(other, False, revs)


def remoteui(repo, opts):
    if hasattr(cmdutil, 'remoteui'):
        # Mercurial 1.5 and lower
        return cmdutil.remoteui(repo, opts)
    else:
        # Mercurial 1.6 and higher
        return hg.remoteui(repo, opts)


def bigpush(push_fn, ui, repo, dest=None, *files, **opts):
    '''Pushes this repository to a target repository.

    If this repository is small, behaves as the native push command.
    For large, remote repositories, the repository is pushed in chunks
    of size optimized for performance on the network.'''
    if not opts.get('chunked'):
        return push_fn(ui, repo, dest, **opts)

    source, revs = parseurl(ui.expandpath(dest or 'default-push', dest or 'default'))
    try:
        other = hg.repository(remoteui(repo, opts), source)
    except:
        # hg 2.3+
        other = hg.peer(repo, opts, source)
    if hasattr(hg, 'addbranchrevs'):
        revs = hg.addbranchrevs(repo, other, revs, opts.get('rev'))[0]
    if revs:
        revs = [repo.lookup(rev) for rev in revs]
    if other.local():
        return push_fn(ui, repo, dest, **opts)

    ui.status(_('pushing to %s\n') % other.path)

    outgoing = findoutgoing(repo, other)
    if outgoing:
        outgoing = repo.changelog.nodesbetween(outgoing, revs)[0]

    # if the push will create multiple heads and isn't forced, fail now
    # (prepush prints an error message, so we can just exit)
    if not opts.get('force') and not opts.get('new_branch') and None == prepush(repo, other, False, revs)[0]:
        return
    try:
        push_size = 1
        while len(outgoing) > 0:
            ui.debug('start: %d to push\n' % len(outgoing))
            current_push_size = min(push_size, len(outgoing))
            ui.debug('pushing: %d\n' % current_push_size)
            # force the push, because we checked above that by the time the whole push is done, we'll have merged back to one head
            remote_heads = repo.push(other, force=True, revs=outgoing[:current_push_size])
            if remote_heads:  # push succeeded
                outgoing = outgoing[current_push_size:]
                ui.debug('pushed %d ok\n' % current_push_size)
                if push_size < max_push_size:
                    push_size *= 2
            else:  # push failed; try again with a smaller size
                push_size /= 2
                ui.debug('failed, trying %d\n' % current_push_size)
                if push_size == 0:
                    raise UnpushableChangesetError
    except UnpushableChangesetError:
        ui.status(_('unable to push changeset %s\n') % outgoing[0])
    ui.debug('done\n')


def parseurl(source):
    '''wrap hg.parseurl to work on 1.3 -> 1.5'''
    return hg.parseurl(source, None)[:2]


def uisetup(ui):
    push_cmd = extensions.wrapcommand(commands.table, 'push', bigpush)
    push_cmd[1].extend([('', 'chunked', None, 'push large repository in chunks')])


class UnpushableChangesetError(Exception):
    pass
