# Copyright (C) 2011-2014 Fog Creek Software.  All rights reserved.
#
# To enable the "kiln" extension put these lines in your ~/.hgrc:
#  [extensions]
#  kiln = /path/to/kiln.py
#
# For help on the usage of "hg kiln" use:
#  hg help kiln
#  hg help -e kiln
#
# This program is free software; you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation; either version 3 of the License, or
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

'''provides command-line support for working with Kiln

This extension allows you to directly open up the Kiln page for your
repository, including the annotation, file view, outgoing, and other
pages.  Additionally, it will attempt to guess which remote Kiln
repository you wish push to and pull from based on its related repositories.

This extension will also notify you when a Kiln server you access has an
updated version of the Kiln Client and Tools available.
To disable the check for a version 'X.Y.Z' and all lower versions, add the
following line in the [kiln] section of your hgrc::

    ignoreversion = X.Y.Z

This extension also lets you create or add changesets to a code review when
pushing to Kiln. See :hg:`help push` and
http://kiln.stackexchange.com/questions/4679/ for more information.
'''

import itertools
import os
import re
import unicodedata
import urllib
import urllib2
import sys
import traceback

from cookielib import MozillaCookieJar
from hashlib import md5
from mercurial import (commands, cmdutil, demandimport, extensions, hg,
                       localrepo, match, util)
from mercurial import ui as hgui
from mercurial import url as hgurl
from mercurial.error import RepoError
from mercurial.i18n import _
from mercurial.node import nullrev

# Mercurial pre-2.9
try:
    from mercurial import scmutil
except ImportError:
    pass

# Mercurial 2.9 - pathauditor moved from scmutil to pathutil
try:
    from mercurial import pathutil
except ImportError:
    pass

demandimport.disable()
try:
    import json
except ImportError:
    sys.path.append(os.path.join(os.path.abspath(os.path.dirname(__file__)), '_custom'))
    import json

try:
    import webbrowser

    def browse(url):
        webbrowser.open(escape_reserved(url))
except ImportError:
    if os.name == 'nt':
        import win32api

        def browse(url):
            win32api.ShellExecute(0, 'open', escape_reserved(url), None, None, 0)
demandimport.enable()

_did_version_check = False


class APIError(Exception):
    def __init__(self, obj):
        '''takes a json object for debugging

        Inspect self.errors to see the API errors thrown.
        '''
        self.errors = dict((e['codeError'], e['sError']) for e in obj['errors'])

    def __str__(self):
        return '\n'.join('%s: %s' % (k, v) for k, v in self.errors.items())


class Review(dict):
    def __init__(self, json, ui=None, token=None, baseurl=None):
        self.ui = ui
        self.token = token
        self.baseurl = baseurl
        self.update(json)

    @property
    def version(self):
        return 2 if 'sReview' in self else 1

    @property
    def key(self):
        return str(self['sReview'] if self.version == 2 else self['ixReview'])

    def belongs_to(self, ixRepo):
        return ixRepo in self['ixRepos'] if self.version == 2 else self['ixRepo'] == ixRepo

    def associate(self, ixRepo, revs):
        if self.version == 1:
            params = {
                'token': self.token,
                'ixBug': self.key,
                'revs': revs,
            }
            call_api(self.ui, self.baseurl, 'Api/1.0/Repo/%d/CaseAssociation/Create' % ixRepo, params, post=True)
        else:
            params = {
                'token': self.token,
                'revs': revs,
                'ixRepo': ixRepo,
            }
            call_api(self.ui, self.baseurl, 'Api/2.0/Review/%s/Association/Create' % self.key, params, post=True)
        return urljoin(self.baseurl, 'Review', self.key)

    @classmethod
    def get_reviews(klass, ui, token, baseurl, ixRepo):
        review_lists = call_api(ui, baseurl, 'Api/2.0/Reviews', dict(token=token, fReviewed="false", fAwaitingReview="false", nDaysActive=14))
        reviews = {}
        for key, review_list in review_lists.iteritems():
            if not key.startswith('reviews'):
                continue
            for review in review_list:
                review = Review(review, ui, token, baseurl)
                if not review.belongs_to(ixRepo):
                    continue
                reviews[review.key.lower()] = review
        return reviews


def urljoin(*components):
    url = components[0]
    for next in components[1:]:
        if not url.endswith('/'):
            url += '/'
        if next.startswith('/'):
            next = next[1:]
        url += next
    return url


def _baseurl(ui, path):
    try:
        url = str(util.url(util.removeauth(path)))
    except util.Abort:
        remote = hg.repository(ui, path)
        try:
            # Mercurial >= 1.9
            url = util.removeauth(remote.url())
        except AttributeError:
            # Mercurial <= 1.8
            url = hgurl.removeauth(remote.url())
    if url.lower().find('/kiln/') > 0 or url.lower().find('kilnhg.com/') > 0 or url.lower().find('kilnhg.dev.local/') > 0:
        return url
    else:
        return None


def escape_reserved(path):
    reserved = re.compile(
        r'^(((com[1-9]|lpt[1-9]|con|prn|aux)(\..*)?)|web\.config' +
        r'|clock\$|app_data|app_code|app_browsers' +
        r'|app_globalresources|app_localresources|app_themes' +
        r'|app_webreferences|bin|.*\.(cs|vb)html?|.*\.(svc|xamlx|xoml|rules))$', re.IGNORECASE)
    p = path.split('?')
    path = p[0]
    query = '?' + p[1] if len(p) > 1 else ''
    return '/'.join('$' + part + '$'
                    if reserved.match(part) or part.startswith('$') or part.endswith('$')
                    else part
                    for part in path.split('/')) + query


def normalize_name(s):
    return s.lower().replace(' ', '-')


def call_api(ui, baseurl, urlsuffix, params, post=False):
    '''returns the json object for the url and the data dictionary

    Uses HTTP POST if the post parameter is True and HTTP GET
    otherwise. Raises APIError on API errors.
    '''
    url = baseurl + urlsuffix
    data = urllib.urlencode(params, doseq=True)
    ui.debug(_('calling %s\n') % url,
             _('    with parameters %s\n') % params)
    try:
        if post:
            fd = urllib2.urlopen(url, data)
        else:
            fd = urllib2.urlopen(url + '?' + data)
        obj = json.load(fd)
    except Exception:
        ui.debug(_('kiln: traceback: %s\n') % traceback.format_exc())
        raise util.Abort(_('kiln: an error occurred while trying to reach %s') % url)

    if isinstance(obj, dict) and 'errors' in obj:
        error_code = obj['errors'][0]['codeError']
        if 'token' in params and error_code == 'InvalidToken':
            token = login(ui, baseurl)
            add_kilnapi_token(ui, baseurl, token)
            params['token'] = token
            return call_api(ui, baseurl, urlsuffix, params, post)
        elif error_code == 'BadAuthentication':
            raise util.Abort(_('authorization failed'))
        raise APIError(obj)
    return obj


def login(ui, url):
    ui.write(_('realm: %s\n') % url)
    user = ui.prompt('username:')
    pw = ui.getpass()

    token = call_api(ui, url, 'Api/1.0/Auth/Login', dict(sUser=user, sPassword=pw))

    if token:
        return token
    raise util.Abort(_('authorization failed'))


def get_domain(url):
    temp = url[url.find('://') + len('://'):]
    domain = temp[:temp.find('/')]
    port = None
    if ':' in domain:
        domain, port = domain.split(':', 1)
    if '.' not in domain:
        domain += '.local'

    return domain


def _get_path(path):
    if os.name == 'nt':
        ret = os.path.expanduser('~\\_' + path)
    else:
        ret = os.path.expanduser('~/.' + path)
    # Cygwin's Python does not always expanduser() properly...
    if re.match(r'^[A-Za-z]:', ret) is not None and re.match(r'[A-Za-z]:\\', ret) is None:
        ret = re.sub(r'([A-Za-z]):', r'\1:\\', ret)
    return ret


def _upgradecheck(ui, repo):
    global _did_version_check
    if _did_version_check or not ui.configbool('kiln', 'autoupdate', True):
        return
    _did_version_check = True
    _upgrade(ui, repo)


def _upgrade(ui, repo):
    ext_dir = os.path.dirname(os.path.abspath(__file__))
    ui.debug(_('kiln: checking for extensions upgrade for %s\n') % ext_dir)

    try:
        r = localrepo.localrepository(hgui.ui(), ext_dir)
    except RepoError:
        commands.init(hgui.ui(), dest=ext_dir)
        r = localrepo.localrepository(hgui.ui(), ext_dir)

    r.ui.setconfig('kiln', 'autoupdate', False)
    r.ui.pushbuffer()
    try:
        source = 'https://developers.kilnhg.com/Repo/Kiln/Group/Kiln-Extensions'
        if commands.incoming(r.ui, r, bundle=None, force=False, source=source) != 0:
            # no incoming changesets, or an error. Don't try to upgrade.
            ui.debug('kiln: no extensions upgrade available\n')
            return
        ui.write(_('updating Kiln Extensions at %s... ') % ext_dir)
        # pull and update return falsy values on success
        if commands.pull(r.ui, r, source=source) or commands.update(r.ui, r, clean=True):
            url = urljoin(repo.url()[:repo.url().lower().index('/repo')], 'Tools')
            ui.write(_('unable to update\nvisit %s to download the newest extensions\n') % url)
        else:
            ui.write(_('complete\n'))
    except Exception, e:
        ui.debug(_('kiln: error updating extensions: %s\n') % e)
        ui.debug(_('kiln: traceback: %s\n') % traceback.format_exc())


def is_dest_a_path(ui, dest):
    paths = ui.configitems('paths')
    for pathname, path in paths:
        if pathname == dest:
            return True
    return False


def is_dest_a_scheme(ui, dest):
    destscheme = dest[:dest.find('://')]
    if destscheme:
        for scheme in hg.schemes:
            if destscheme == scheme:
                return True
    return False


def create_match_list(matchlist):
    ret = ''
    for m in matchlist:
        ret += '    ' + m + '\n'
    return ret


def get_username(url):
    url = re.sub(r'https?://', '', url)
    url = re.sub(r'/.*', '', url)
    if '@' in url:
        # There should be some login info
        # rfind in case it's an email address
        username = url[:url.rfind('@')]
        if ':' in username:
            username = url[:url.find(':')]
        return username
    # Didn't find anything...
    return ''


def get_dest(ui):
    from mercurial.dispatch import _parse
    try:
        cmd_info = _parse(ui, sys.argv[1:])
        cmd = cmd_info[0]
        dest = cmd_info[2]
        if dest:
            dest = dest[0]
        elif cmd in ['outgoing', 'push']:
            dest = 'default-push'
        else:
            dest = 'default'
    except:
        dest = 'default'
    return ui.expandpath(dest)


def check_kilnapi_token(ui, url):
    tokenpath = _get_path('hgkiln')

    if (not os.path.exists(tokenpath)) or os.path.isdir(tokenpath):
        return ''

    domain = get_domain(url)
    userhash = md5(get_username(get_dest(ui))).hexdigest()

    fp = open(tokenpath, 'r')
    ret = ""
    for line in fp:
        try:
            d, u, t = line.split(' ')
        except:
            raise util.Abort(_('Authentication file %s is malformed.') % tokenpath)
        if d == domain and u == userhash:
            # Get rid of that newline character...
            ret = t[:-1]

    fp.close()
    return ret


def add_kilnapi_token(ui, url, fbToken):
    if not fbToken:
        return
    tokenpath = _get_path('hgkiln')
    if os.path.isdir(tokenpath):
        raise util.Abort(_('Authentication file %s exists, but is a directory.') % tokenpath)

    domain = get_domain(url)
    userhash = md5(get_username(get_dest(ui))).hexdigest()

    fp = open(tokenpath, 'a')
    fp.write(domain + ' ' + userhash + ' ' + fbToken + '\n')
    fp.close()


def delete_kilnapi_tokens():
    # deletes the hgkiln file
    tokenpath = _get_path('hgkiln')
    if os.path.exists(tokenpath) and not os.path.isdir(tokenpath):
        os.remove(tokenpath)


def check_kilnauth_token(ui, url):
    cookiepath = _get_path('hgcookies')
    if (not os.path.exists(cookiepath)) or (not os.path.isdir(cookiepath)):
        return ''
    cookiepath = os.path.join(cookiepath, md5(get_username(get_dest(ui))).hexdigest())

    try:
        if not os.path.exists(cookiepath):
            return ''
        cj = MozillaCookieJar(cookiepath)
    except IOError:
        return ''

    domain = get_domain(url)

    cj.load(ignore_discard=True, ignore_expires=True)
    for cookie in cj:
        if domain == cookie.domain:
            if cookie.name == 'fbToken':
                return cookie.value

# Get the path auditor available for the current version of Mercurial.
# Mercurial moved this class in versions 1.9 and 2.9.
def build_audit_path(repo):
    try:
        # Mercurial 2.9
        return pathutil.pathauditor(repo.root)
    except ImportError:
        try:
            # Mercurial 1.9 to 2.9
            return scmutil.pathauditor(repo.root)
        except ImportError:
            # Mercurial < 1.9
            return getattr(repo.opener, 'audit_path', util.path_auditor(repo.root))



def remember_path(ui, repo, path, value):
    '''appends the path to the working copy's hgrc and backs up the original'''

    paths = dict(ui.configitems('paths'))
    # This should never happen.
    if path in paths:
        return
    # ConfigParser only cares about these three characters.
    if re.search(r'[:=\s]', path):
        return

    audit_path = build_audit_path(repo)
    audit_path('hgrc')
    audit_path('hgrc.backup')
    base = repo.opener.base

    hgrc, backup = [os.path.join(base, x) for x in 'hgrc', 'hgrc.backup']
    if os.path.exists(hgrc):
        util.copyfile(hgrc, backup)

    ui.setconfig('paths', path, value)

    try:
        fp = repo.opener('hgrc', 'a', text=True)
        # Mercurial assumes Unix newlines by default and so do we.
        fp.write('\n[paths]\n%s = %s\n' % (path, value))
        fp.close()
    except IOError:
        return


def unremember_path(ui, repo):
    '''restores the working copy's hgrc'''

    audit_path = build_audit_path(repo)
    audit_path('hgrc')
    audit_path('hgrc.backup')
    base = repo.opener.base

    hgrc, backup = [os.path.join(base, x) for x in 'hgrc', 'hgrc.backup']
    if os.path.exists(backup):
        util.copyfile(backup, hgrc)
    else:
        os.remove(hgrc)


def guess_kilnpath(orig, ui, repo, dest=None, **opts):
    if not dest:
        return orig(ui, repo, **opts)

    if os.path.exists(dest) or is_dest_a_path(ui, dest) or is_dest_a_scheme(ui, dest):
        return orig(ui, repo, dest, **opts)
    else:
        targets = get_targets(repo)
        matches = []
        prefixmatches = []

        for target in targets:
            url = '%s/%s/%s/%s' % (target[0], target[1], target[2], target[3])
            ndest = normalize_name(dest)
            ntarget = [normalize_name(t) for t in target[1:4]]
            aliases = [normalize_name(s) for s in target[4]]

            if (ndest.count('/') == 0 and
                (ntarget[0] == ndest or
                 ntarget[1] == ndest or
                 ntarget[2] == ndest or
                 ndest in aliases)):
                matches.append(url)
            elif (ndest.count('/') == 1 and
                    '/'.join(ntarget[0:2]) == ndest or
                    '/'.join(ntarget[1:3]) == ndest):
                matches.append(url)
            elif (ndest.count('/') == 2 and
                    '/'.join(ntarget[0:3]) == ndest):
                matches.append(url)

            if (ntarget[0].startswith(ndest) or
                    ntarget[1].startswith(ndest) or
                    ntarget[2].startswith(ndest) or
                    '/'.join(ntarget[0:2]).startswith(ndest) or
                    '/'.join(ntarget[1:3]).startswith(ndest) or
                    '/'.join(ntarget[0:3]).startswith(ndest)):
                prefixmatches.append(url)

        if len(matches) == 0:
            if len(prefixmatches) == 0:
                # if there are no matches at all, let's just let mercurial handle it.
                return orig(ui, repo, dest, **opts)
            else:
                urllist = create_match_list(prefixmatches)
                raise util.Abort(_('%s did not exactly match any part of the repository slug:\n\n%s') % (dest, urllist))
        elif len(matches) > 1:
            urllist = create_match_list(matches)
            raise util.Abort(_('%s matches more than one Kiln repository:\n\n%s') % (dest, urllist))

        # Unique match -- perform the operation
        try:
            remember_path(ui, repo, dest, matches[0])
            return orig(ui, repo, matches[0], **opts)
        finally:
            unremember_path(ui, repo)


def get_tails(repo):
    tails = []
    for rev in xrange(repo['tip'].rev() + 1):
        ctx = repo[rev]
        if ctx.p1().rev() == nullrev and ctx.p2().rev() == nullrev:
            tails.append(ctx.hex())
    if not len(tails):
        raise util.Abort(_('Path guessing is only enabled for non-empty repositories.'))
    return tails


def get_targets(repo):
    targets = set()
    urls = repo.ui.configitems('paths')
    for tup in urls:
        url = tup[1]
        baseurl = get_api_url(url)
        if baseurl is None or baseurl.startswith('ssh:'):
            continue

        tails = get_tails(repo)
        token = get_token(repo.ui, baseurl)

        # We have an token at this point
        params = dict(revTails=tails, token=token)
        related_repos = call_api(repo.ui, baseurl, 'Api/1.0/Repo/Related', params)
        stripped = baseurl.rstrip('/')
        if not stripped.startswith('ssh:'):
            stripped += "/Code"
        related = [(stripped,
                    related_repo['sProjectSlug'],
                    related_repo['sGroupSlug'],
                    related_repo['sSlug'],
                    tuple(a['sSlug'] for a in related_repo.get('rgAliases', []))) for related_repo in related_repos]
        targets = targets.union(related)
    out = list(targets)
    out.sort()
    return out


def display_targets(repo):
    targets = get_targets(repo)
    repo.ui.write(_('The following Kiln targets are available for this repository:\n\n'))
    for target in targets:
        if target[4]:
            alias_text = _(' (alias%s: %s)') % ('es' if len(target[4]) > 1 else '', ', '.join(target[4]))
        else:
            alias_text = ''
        repo.ui.write('    %s/%s/%s/%s%s\n' % (target[0], target[1], target[2], target[3], alias_text))


def get_token(ui, url):
    '''Checks for an existing API token. If none, returns a new valid token.'''
    token = check_kilnapi_token(ui, url)
    if not token:
        token = check_kilnauth_token(ui, url)
        add_kilnapi_token(ui, url, token)
    if not token:
        token = login(ui, url)
        add_kilnapi_token(ui, url, token)
    return token


def get_api_url(url):
    '''Given a URL, returns the URL of the Kiln installation.'''
    if 'kilnhg.com/' in url.lower():
        baseurl = url[:url.lower().find('kilnhg.com/') + 11]
    elif '/kiln/' in url.lower():
        baseurl = url[:url.lower().find('/kiln/') + 6]
    elif 'kilnhg.dev.local/' in url.lower():
        baseurl = url[:url.lower().find('kilnhg.dev.local/') + 17]
    else:
        baseurl = None
    return baseurl


class HTTPNoRedirectHandler(urllib2.HTTPRedirectHandler):
    def http_error_302(self, req, fp, code, msg, headers):
        # Doesn't allow multiple redirects so repo alias URLs will not
        # eventually get redirected to the unhelpful login page
        return fp

    http_error_301 = http_error_303 = http_error_307 = http_error_302


def get_repo_record(repo, url, token=None):
    '''Returns a Kiln repository record that corresponds to the given repo.'''
    baseurl = get_api_url(url)
    if not token:
        token = get_token(repo.ui, baseurl)

    try:
        data = urllib.urlencode({'token': token}, doseq=True)
        opener = urllib2.build_opener(HTTPNoRedirectHandler)
        urllib2.install_opener(opener)
        fd = urllib2.urlopen(url + '?' + data)

        # Get redirected URL
        if 'location' in fd.headers:
            url = fd.headers.getheaders('location')[0]
        elif 'uri' in fd.headers:
            url = fd.headers.getheaders('uri')[0]
    except urllib2.HTTPError:
        raise util.Abort(_('Invalid URL: %s') % url)

    def find_slug(slug, l, attr=None):
        if not l:
            return None
        for candidate in l.get(attr) if attr else l:
            if candidate['sSlug'] == slug or (slug == 'Group' and candidate['sSlug'] == ''):
                return candidate
        return None

    paths = url.split('/')
    kiln_projects = call_api(repo.ui, baseurl, 'Api/1.0/Project/', dict(token=token))
    project, group, repo = paths[-3:]
    project = find_slug(project, kiln_projects)
    group = find_slug(group, project, 'repoGroups')
    repo = find_slug(repo, group, 'repos')
    return repo


def new_branch(repo, url, name):
    '''Creates a new, decentralized branch off of the specified repo.'''
    baseurl = get_api_url(url)
    token = get_token(repo.ui, baseurl)
    kiln_repo = get_repo_record(repo, url, token)
    params = {'sName': name,
              'ixRepoGroup': kiln_repo['ixRepoGroup'],
              'ixParent': kiln_repo['ixRepo'],
              'fCentral': False,
              'sDefaultPermission': 'inherit',
              'token': token}
    repo.ui.write(_('branching from %s\n') % url)
    try:
        return call_api(repo.ui, baseurl, 'Api/1.0/Repo/Create', params, post=True)
    except APIError, e:
        if 'RepoNameAlreadyUsed' in e.errors:
            repo.ui.warn(_('error: kiln: a repo with this name already exists: %s\n') % name)
            return
        raise


def normalize_user(s):
    '''Takes a Unicode string and returns an ASCII string.'''
    return unicodedata.normalize('NFKD', s).encode('ASCII', 'ignore')


def encode_out(s):
    '''Takes a Unicode string and returns a string encoded for output.'''
    return s.encode(sys.stdout.encoding, 'ignore')


def record_base(ui, repo, node, **kwargs):
    '''Stores the first changeset committed in the repo UI so we do not need to expensively recalculate.'''
    repo.ui.setconfig('kiln', 'node', node)


def walk(repo, revs):
    '''Returns revisions in repo specified by the string revs'''
    return cmdutil.walkchangerevs(repo, match.always(repo.root, None), {'rev': [revs.encode('ascii', 'ignore')]}, lambda *args: None)


def print_list(ui, l, header):
    '''Prints a list l to ui using list notation, with header being the first line'''
    ui.write(_('%s\n' % header))
    for item in l:
        ui.write(_('- %s\n') % item)


def wrap_push(orig, ui, repo, dest=None, **opts):
    '''Wraps `hg push' so a review will be created after path guessing and a successful push.'''
    guess_kilnpath(orig, ui, repo, dest, **opts)
    review(ui, repo, dest, opts)


def add_unique_reviewer(ui, reviewer, reviewers, name_to_ix, ix_to_name):
    '''Adds a reviewer to reviewers if it is not already added. Otherwise, print an error.'''
    if name_to_ix[reviewer] in reviewers:
        ui.write(_('user already added: %s\n') % ix_to_name[name_to_ix[reviewer]])
    else:
        reviewers.append(name_to_ix[reviewer])
        print_list(ui, [ix_to_name[r] for r in reviewers], 'reviewers:')


def review(ui, repo, dest, opts):
    '''Associates the pushed changesets with a new or existing Kiln review.'''
    if not opts['review'] or not repo.ui.config('kiln', 'node'):
        return

    url = ui.expandpath(dest or 'default-push', dest or 'default')
    baseurl = get_api_url(url)
    if baseurl == url:
        ui.write_err(_('kiln: warning: this does not appear to be a Kiln URL: %s\n') % baseurl)

    token = get_token(ui, baseurl)
    kiln_repo = get_repo_record(repo, url, token)
    reviews = Review.get_reviews(repo.ui, token, baseurl, kiln_repo['ixRepo'])

    choices = []
    ui.write(_('\n'))
    for review_key in sorted(reviews.iterkeys()):
        review = reviews[review_key]
        title = re.sub('\s+', ' ', review['sTitle'])
        ui.write(encode_out(_('%s - %s\n') % (review_key, title)))
        choices.append(review_key)

    choices.extend(['n', 'q', '?'])
    while True:
        choice = ui.prompt(_('add to review? [nq?]')).lower()
        if choice not in choices:
            ui.write(_('unrecognized response\n\n'))
        elif choice == 'q':
            return
        elif choice == '?':
            exist_review = _('  - enter an existing review number to add changeset(s).\n') if reviews else ''
            ui.write(exist_review)
            ui.write(_('n - new, create a new review.\n'),
                     _('q - quit, do not associate changeset(s).\n'),
                     _('? - display help.\n\n'))
        else:
            # Create new review or associate changeset(s) to existing
            break

    node = repo.ui.config('kiln', 'node')
    heads = opts['rev']
    # If no specified revisions to push, default to getting revision numbers (not ancestors/descendants) between node and tip.
    sets = ['%s::%s' % (node, r) for r in heads] if heads else ['%s:tip' % node]
    revset = ' or '.join(sets)
    revs = [r.hex() for r in walk(repo, revset)]

    if choice == 'n':
        # Associate changeset(s) with a new review.
        people_records = call_api(repo.ui, baseurl, 'Api/1.0/Person', dict(token=token))
        # If two user names normalize to the same string, then name_to_ix will only store the second person. This will
        # also affect user input if the user enters the first user's unstored name, then the user will add the wrong
        # reviewer. If this edge case becomes an issue, I wish thee happy pondering.
        name_to_ix = dict([(normalize_user(p['sName']).lower(), p['ixPerson']) for p in people_records])
        ix_to_name = dict([(p['ixPerson'], encode_out(p['sName'])) for p in people_records])

        reviewers = []
        while True:
            reviewer = ui.prompt(_('\nchoose reviewer(s) [dlq?]'), default='')
            reviewer = normalize_user(unicode(reviewer, sys.stdin.encoding)).lower()
            if reviewer == 'q':
                return
            elif reviewer == '?':
                ui.write(_('  - type a user\'s full name to add that user. a partial name displays\n'),
                         _('    a list of matching users.\n'),
                         _('d - done, create a new review.\n'),
                         _('l - list, list current reviewers.\n'),
                         _('q - quit, do not create a new review.\n'),
                         _('? - display help.\n'))
            elif reviewer == 'l':
                if reviewers:
                    print_list(ui, [ix_to_name[r] for r in reviewers], 'reviewers:')
                else:
                    ui.write(_('no users selected.\n'))
            elif reviewer == 'd':
                if reviewers:
                    break
                else:
                    ui.write(_('no users selected.\n'))
            elif reviewer in name_to_ix.keys():
                add_unique_reviewer(ui, reviewer, reviewers, name_to_ix, ix_to_name)
            else:
                options = filter(lambda name: reviewer in name, name_to_ix.keys())
                options = [ix_to_name[name_to_ix[name]] for name in options]
                options = sorted(options, key=lambda n: n.lower())
                if options:
                    if len(options) == 1:
                        # If one user matches search, just add him/her
                        option = normalize_user(unicode(options[0], sys.stdout.encoding)).lower()
                        add_unique_reviewer(ui, option, reviewers, name_to_ix, ix_to_name)
                    else:
                        print_list(ui, options, 'user names (%d) that match:' % len(options))
                else:
                    ui.write(_('no matching users.\n'))

        params = {
            'token': token,
            'ixRepo': kiln_repo['ixRepo'],
            'revs': revs,
            'ixReviewers': reviewers,
            'sTitle': '(Multiple changesets)' if len(revs) > 1 else repo[revs[0]].description(),
            'sDescription': 'Review created from push.'
        }
        r = Review(call_api(repo.ui, baseurl, 'Api/2.0/Review/Create', params, post=True))
        ui.write(_('new review created: %s\n' % urljoin(baseurl, 'Review', r.key)))
    else:
        # Associate changeset(s) with an existing review.
        url = reviews[choice].associate(kiln_repo['ixRepo'], revs)
        ui.write(_('updated review: %s\n' % url))


def dummy_command(ui, repo, dest=None, **opts):
    '''dummy command to pass to guess_path() for hg kiln

    Returns the repository URL if dest has been successfully path
    guessed, None otherwise.
    '''
    return opts['path'] != dest and dest or None


def _standin_expand(paths):
    '''given a sequence of filenames, returns a set of filenames --
    relative to the current working directory! -- prefixed with all
    possible standin prefixes e.g. .hglf or .kbf in addition to the
    originals'''
    paths = [os.path.relpath(os.path.abspath(p), os.getcwd()) for p in paths]
    choices = [[p, os.path.join('.kbf', p), os.path.join('.hglf', p)] for p in paths]
    return set(itertools.chain(*choices))


def _filename_match(repo, ctx, paths):
    '''returns a set of filenames contained in both paths and the
    ctx's manifest, accounting for standins'''
    try:
        match = scmutil.match(ctx, paths)
        match.bad = lambda *a: None
        paths = set(ctx.walk(match))
        return paths
    except ImportError:
        # Make every path normalized and relative to the current
        # working directory, similar to scmutil.
        needles = set(map(os.path.normpath, paths))
        haystacks = [os.path.relpath(os.path.join(repo.root, p), os.getcwd())
                     for p in ctx.manifest().iterkeys()]
        haystacks = set(map(os.path.normpath, haystacks))
        return needles.intersection(haystacks)


def kiln(ui, repo, **opts):
    '''show the relevant page of the repository in Kiln

    This command allows you to navigate straight to the Kiln page for a
    repository, including directly to settings, file annotation, and
    file & changeset viewing.

    Typing "hg kiln" by itself will take you directly to the
    repository history in kiln.  Specify any other options to override
    this default. The --rev, --annotate, --file, and --filehistory options
    can be used together.

    To display a list of valid targets, type hg kiln --targets.  To
    push or pull from one of these targets, use any unique identifier
    from this list as the parameter to the push/pull command.
    '''

    try:
        url = _baseurl(ui, ui.expandpath(opts['path'] or 'default', opts['path'] or 'default-push'))
    except RepoError:
        url = guess_kilnpath(dummy_command, ui, repo, dest=opts['path'], **opts)
        if not url:
            raise

    if not url:
        raise util.Abort(_('this does not appear to be a Kiln-hosted repository\n'))
    default = True

    def files(key):
        paths = _filename_match(repo, repo['.'], _standin_expand(opts[key]))
        if not paths:
            ui.warn(_('error: kiln: cannot find any paths matching %s\n') % ', '.join(opts[key]))
        if len(paths) > 5:
            # If we're passed a directory, we should technically open
            # a tab for each file in that directory because that's how
            # other hg commands e.g. cat work. However, since that's
            # quite annoying to do by accident when opening browsers,
            # let's prompt. (This is only relevant when scmutil
            # exists.)
            char = ui.prompt(_('about to open %d browser tabs or windows, abort? [Yn]') % len(paths)).lower()
            if char != 'n':
                raise SystemExit(0)
        return paths

    if opts['rev']:
        default = False
        for ctx in (repo[rev] for rev in opts['rev']):
            browse(urljoin(url, 'History', ctx.hex()))

    if opts['annotate']:
        default = False
        for f in files('annotate'):
            browse(urljoin(url, 'Files', f) + '?view=annotate')
    if opts['file']:
        default = False
        for f in files('file'):
            browse(urljoin(url, 'Files', f))
    if opts['filehistory']:
        default = False
        for f in files('filehistory'):
            browse(urljoin(url, 'FileHistory', f) + '?rev=tip')

    if opts['outgoing']:
        default = False
        browse(urljoin(url, 'Outgoing'))
    if opts['settings']:
        default = False
        browse(urljoin(url, 'Settings'))

    if opts['targets']:
        default = False
        display_targets(repo)
    if opts['new_branch']:
        default = False
        new_branch(repo, url, opts['new_branch'])
    if opts['logout']:
        default = False
        delete_kilnapi_tokens()

    if default or opts['changes']:
        browse(url)


def uisetup(ui):
    extensions.wrapcommand(commands.table, 'outgoing', guess_kilnpath)
    extensions.wrapcommand(commands.table, 'pull', guess_kilnpath)
    extensions.wrapcommand(commands.table, 'incoming', guess_kilnpath)
    push_cmd = extensions.wrapcommand(commands.table, 'push', wrap_push)
    # Add --review as a valid flag to push's command table
    push_cmd[1].extend([('', 'review', None, 'associate changesets with Kiln review')])


def reposetup(ui, repo):
    try:
        from mercurial.httprepo import httprepository
        httprepo = httprepository
    except (ImportError, AttributeError):
        from mercurial.httppeer import httppeer
        httprepo = httppeer
    if issubclass(repo.__class__, httprepo):
        _upgradecheck(ui, repo)
    repo.ui.setconfig('hooks', 'outgoing.kilnreview', 'python:kiln.record_base')


def extsetup(ui):
    try:
        f = extensions.find('fetch')
        extensions.wrapcommand(f.cmdtable, 'fetch', guess_kilnpath)
    except KeyError:
        pass

cmdtable = {
    'kiln':
        (kiln,
         [('a', 'annotate', [], _('annotate the file provided')),
          ('c', 'changes', None, _('view the history of this repository; this is the default')),
          ('f', 'file', [], _('view the file contents')),
          ('l', 'filehistory', [], _('view the history of the file')),
          ('o', 'outgoing', None, _('view the repository\'s outgoing tab')),
          ('s', 'settings', None, _('view the repository\'s settings tab')),
          ('p', 'path', '', _('select which Kiln branch of the repository to use')),
          ('r', 'rev', [], _('view the specified changeset in Kiln')),
          ('t', 'targets', None, _('view the repository\'s targets')),
          ('n', 'new-branch', '', _('asynchronously create a new branch from the current repository')),
          ('', 'logout', None, _('log out of Kiln sessions'))],
         _('hg kiln [-p url] [-r rev|-a file|-f file|-c|-o|-s|-t|-n branchName|--logout]'))
}
