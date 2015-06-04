# Copyright (C) 2009-2013 Fog Creek Software.  All rights reserved.
#
# To enable the "kilnpath" extension put these lines in your ~/.hgrc:
#    [extensions]
#    kilnpath = /path/to/kilnpath.py
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

'''allow users identify repository paths by custom schemes like kiln://Project/Group/Repository (DEPRECATED)

This extensions knows how to expand Kiln repository paths from
kiln://Project/Group/Repository to
the full http://your.fogbugz.com/kiln/Repo/Project/Group/Repository,
so you only have to type
"hg pull kiln://Project/Group/Repository" at the command line.

This extensions works for pull, push, incoming, outgoing, and clone.

To specify the Kiln path prefix, add a [kiln_scheme] section to your hg config like so:
[kiln_scheme]
kiln = https://your.fogbugz.com/kiln/Repo

This extensions is deprecated, and does not work in Mercurial 2.4 and later.
You should use the built-in `schemes` extension instead instead.  Type `hg help schemes`
for more information.
'''

import re

from mercurial import hg, ui, util, __version__


@apply
def _HG_VERSION():
    '''return the mercurial version as a tuple rather than a string

    Python does the right thing when comparing tuples, so the return
    value can be used to compare and detect versions.
    '''
    version = [0, 0, 0]
    parts = [re.match(r'\d+', v).group(0) for v in __version__.version.split('.')[:3]]
    for i, part in enumerate(map(int, parts)):
        version[i] = part
    return tuple(version)


if _HG_VERSION >= (2, 3, 0):
    raise util.Abort(
        'kilnpath is deprecated, and does not work in Mercurial 2.3 or higher.  '
        'Use the official schemes extension instead')


def urlcombine(pre, suff):
    if pre.endswith("/"):
        pre = pre[0:len(pre) - 1]
    if not suff.startswith("/"):
        suff = "/" + suff
    return pre + suff


def add_schemes(ui):
    schemes = ui.configitems('kiln_scheme')
    for scheme, prefix in schemes:
        hg.schemes[scheme] = KilnRepo(scheme, prefix)


class KilnRepo(object):
    def __init__(self, scheme, prefix):
        self.scheme = scheme
        self.prefix = prefix

    def instance(self, ui, path, create):
        path_pieces = path.split(":", 1)
        if len(path_pieces) <= 1:
            return None

        suffix = path_pieces[1]
        if suffix.startswith("//"):
            suffix = suffix[2:]

        path_full = urlcombine(self.prefix, suffix)
        try:
            # Mercurial >= 1.9
            return hg._peerlookup(path_full).instance(ui, path_full, create)
        except AttributeError:
            # Mercurial <= 1.8
            return hg._lookup(path_full).instance(ui, path_full, create)


def extsetup(*args):
    if len(args) > 0:
        add_schemes(args[0])
    else:
        add_schemes(ui.ui())
