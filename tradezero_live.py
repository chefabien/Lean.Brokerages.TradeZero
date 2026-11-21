#!/usr/bin/env python3
"""Prepare a LEAN live-trading run with the TradeZero brokerage plugin and a market-data feed.

The official quantconnect/lean image is used as is, at a fixed version tag. This script:
  1. picks the Lean version: the local quantconnect/lean image's version if a plugin release asset exists for
     it or an older Lean version, otherwise the newest Lean version an asset exists for (pulling that fixed tag);
  2. downloads the TradeZero plugin release asset (tradezero-plugin-lean<N>.zip) built for that Lean version
     or the closest older one, if needed;
  3. fetches the data feed plugin from nuget.org, built for that Lean version or the closest older one;
  4. writes config.json and run.sh in the output folder, and prints the docker command to start LEAN.

TradeZero keys are never written: run.sh passes TZ_API_KEY_ID / TZ_API_SECRET_KEY from your environment.
The data feed's own keys go into config.json (mode 0600): LEAN only reads them from its config.

QuantConnect's data feed modules (Polygon, Databento, Alpaca) check at startup that your QuantConnect account
is entitled to them, as they do under lean-cli. The launcher therefore also writes your QuantConnect user id,
API token and organization id into config.json (from QC_USER_ID / QC_API_ACCESS_TOKEN / QC_ORGANIZATION_ID,
or from lean-cli's ~/.lean/credentials and the lean.json of a workspace given with --workspace).
With --workspace, the workspace's data folder (lean.json "data-folder", map and factor files included) is
mounted at /Lean/Data instead of the sample data inside the image, as lean-cli does.

Python 3.9+, standard library only. Needs docker on the PATH.
"""

from __future__ import annotations

import argparse
import getpass
import hashlib
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

LEAN_IMAGE_REPO = "quantconnect/lean"
LEAN_BIN = "/Lean/Launcher/bin/Debug"
ASSET_PATTERN = re.compile(r"^tradezero-plugin-lean(\d+)\.zip$")
NUGET_INDEX = "https://api.nuget.org/v3-flatcontainer/{package}/index.json"
GITHUB_API = os.environ.get("TZ_GITHUB_API_URL", "https://api.github.com").rstrip("/")  # override: tests only
HERE = Path(__file__).resolve().parent
CONFIG_TEMPLATE = HERE / "config.tradezero.json"
USER_AGENT = "lean-tradezero-launcher"


@dataclass(frozen=True)
class Setting:
    """A config value of a data feed, read from an environment variable or asked for."""
    key: str
    env: str
    secret: bool = True
    default: str | None = None
    choices: tuple[str, ...] = ()


@dataclass(frozen=True)
class Feed:
    name: str
    description: str
    package: str | None  # nuget.org package id, None for a feed built into Lean
    data_queue_handler: str
    history_provider: str
    settings: tuple[Setting, ...] = ()
    extra_config: dict = field(default_factory=dict)
    testing_only: bool = False
    # QuantConnect's feed modules check at startup, through the QuantConnect API, that the account in
    # job-user-id / api-access-token / job-organization-id is entitled to them (as with lean-cli)
    needs_quantconnect_account: bool = True


# Class names and config keys: QuantConnect's module catalog (cdn.quantconnect.com/cli/modules-*.json),
# checked against the keys the feed DLLs read.
FEEDS = {
    "polygon": Feed(
        "polygon", "Polygon.io (US stocks)", "QuantConnect.Lean.DataSource.Polygon",
        "QuantConnect.Lean.DataSource.Polygon.PolygonDataProvider",
        "QuantConnect.Lean.DataSource.Polygon.PolygonDataProvider",
        (Setting("polygon-api-key", "POLYGON_API_KEY"),
         Setting("polygon-license-type", "POLYGON_LICENSE_TYPE", secret=False, default="Individual",
                 choices=("Individual", "Business")))),
    "databento": Feed(
        "databento", "Databento (US equities)", "QuantConnect.Lean.DataSource.DataBento",
        "QuantConnect.Lean.DataSource.DataBento.DataBentoDataProvider",
        "QuantConnect.Lean.DataSource.DataBento.DataBentoDataProvider",
        (Setting("databento-api-key", "DATABENTO_API_KEY"),)),
    "alpaca": Feed(
        "alpaca", "Alpaca market data (free IEX feed with an Alpaca account)", "QuantConnect.Brokerages.Alpaca",
        "AlpacaBrokerage",
        # Lean wraps the AlpacaBrokerage data feed as the history provider for this name
        "QuantConnect.Lean.Engine.HistoricalData.AlpacaBrokerageHistoryProvider",
        (Setting("alpaca-api-key", "ALPACA_API_KEY"),
         Setting("alpaca-api-secret", "ALPACA_API_SECRET"),
         Setting("alpaca-environment", "ALPACA_ENVIRONMENT", secret=False, default="paper",
                 choices=("paper", "live")))),
    "fake": Feed(
        "fake", "Lean's FakeDataQueue: random prices, for testing only", None,
        "QuantConnect.Lean.Engine.DataFeeds.Queues.FakeDataQueue",
        "QuantConnect.Lean.Engine.HistoricalData.SubscriptionDataReaderHistoryProvider",
        extra_config={"force-exchange-always-open": True}, testing_only=True, needs_quantconnect_account=False),
}

QC_CREDENTIALS_FILE = Path.home() / ".lean" / "credentials"  # written by `lean login`
QC_SETTINGS = (  # Lean config key, environment variable, key in ~/.lean/credentials or lean.json
    ("job-user-id", "QC_USER_ID", "user-id"),
    ("api-access-token", "QC_API_ACCESS_TOKEN", "api-token"),
    ("job-organization-id", "QC_ORGANIZATION_ID", "job-organization-id"),
)


class LauncherError(Exception):
    pass


def log(message: str) -> None:
    print(f"[tradezero-live] {message}", file=sys.stderr)


# ------------------------------------------------------------------------------------------- pure helpers

def strip_json_comments(text: str) -> str:
    """Removes // and /* */ comments outside of JSON strings (Lean's config files allow them)."""
    pattern = re.compile(r'("(?:\\.|[^"\\])*")|//[^\n]*|/\*.*?\*/', re.DOTALL)
    return pattern.sub(lambda m: m.group(1) or "", text)


def assets_by_lean_version(releases: list[dict], plugin_version: str | None = None) -> dict[int, dict]:
    """Maps a Lean version to the plugin asset to use for it: from the newest release (GitHub lists newest
    first) unless plugin_version names a release. Drafts are ignored; each entry keeps its release tag and
    the matching .sha256 asset when there is one."""
    found: dict[int, dict] = {}
    for release in releases:
        if release.get("draft"):
            continue
        if plugin_version and release.get("tag_name") != plugin_version:
            continue
        assets = {a["name"]: a for a in release.get("assets", [])}
        for name, asset in assets.items():
            match = ASSET_PATTERN.match(name)
            if not match:
                continue
            version = int(match.group(1))
            if version not in found:
                found[version] = {"tag": release.get("tag_name"), "asset": asset,
                                  "sha256": assets.get(name + ".sha256")}
    return found


def closest_not_newer(versions, lean_version: int) -> int | None:
    """Highest version <= lean_version: the build made for that Lean version or the closest older one."""
    candidates = [v for v in versions if v <= lean_version]
    return max(candidates) if candidates else None


def choose_lean_version(local_versions: list[int], supported: list[int],
                        requested: int | None = None) -> tuple[int, int, str]:
    """Returns (Lean version to run, Lean version of the plugin asset to use, why). Like the data feeds, the
    plugin is the one built for that Lean version or the closest older one. Never a floating tag."""
    if not supported:
        raise LauncherError("No TradeZero plugin release asset found (tradezero-plugin-lean<V>.zip).")
    if requested is not None:
        plugin = closest_not_newer(supported, requested)
        if plugin is None:
            raise LauncherError(f"No TradeZero plugin release for Lean {requested} or older. "
                                f"Available: {format_versions(supported)}")
        return requested, plugin, f"Lean {requested} (requested)"
    if local_versions:
        local = max(local_versions)
        plugin = closest_not_newer(supported, local)
        if plugin is not None:
            return local, plugin, f"Lean {local} (local image)"
        newest = max(supported)
        return newest, newest, (f"Lean {newest}: the local image is Lean {local}, older than every TradeZero plugin "
                                f"release; using the newest supported version")
    newest = max(supported)
    return newest, newest, f"Lean {newest} (no local {LEAN_IMAGE_REPO} image: newest version with a plugin release)"


def choose_feed_version(versions: list[str], lean_version: int) -> str | None:
    """Highest 2.5.N package version built for Lean N <= lean_version (feeds are not built for every Lean build)."""
    best = None
    for version in versions:
        match = re.fullmatch(r"2\.5\.(\d+)", version)
        if match and int(match.group(1)) <= lean_version and (best is None or int(match.group(1)) > best[0]):
            best = (int(match.group(1)), version)
    return best[1] if best else None


def format_versions(versions) -> str:
    return ", ".join(str(v) for v in sorted(versions, reverse=True)) or "none"


def detect_algorithm_class(main_py: Path) -> str | None:
    match = re.search(r"^class\s+(\w+)\s*\(\s*QCAlgorithm\s*\)", main_py.read_text(encoding="utf-8"), re.MULTILINE)
    return match.group(1) if match else None


def build_config(template: dict, feed: Feed, feed_values: dict, *, algorithm_name: str, language: str,
                 algorithm_location: str, environment: str, api_url: str | None, extra: dict) -> dict:
    config = json.loads(json.dumps(template))  # deep copy
    config.update({
        "environment": "live-tradezero",
        "algorithm-type-name": algorithm_name,
        "algorithm-language": "Python" if language == "python" else "CSharp",
        "algorithm-location": algorithm_location,
        "plugin-directory": "/Lean/plugins",
        "object-store-root": "/Storage",  # the object store, mounted read-write like lean-cli does
        "data-queue-handler": [feed.data_queue_handler],
        "history-provider": [feed.history_provider],
        "tradezero-environment": environment,
        "tradezero-api-key-id": "",
        "tradezero-api-secret-key": "",
    })
    if api_url:
        config["tradezero-api-url"] = api_url
    config.update(feed.extra_config)
    config.update(feed_values)
    config.update(extra)
    return config


def feed_config_values(feed: Feed, raw: dict) -> dict:
    """Turns the user's answers into Lean config values (Alpaca's paper/live becomes alpaca-paper-trading)."""
    values = dict(raw)
    if feed.name == "alpaca":
        values["alpaca-paper-trading"] = values.pop("alpaca-environment", "paper") == "paper"
    return values


VALUE_FLAGS = {"--name", "--network", "-v", "-e", "--config"}


def format_command(command: list[str], indent: str = "  ") -> list[str]:
    """Groups the command for display: one line per flag with its value, the image on its own line."""
    lines, current, i = [], [], 0
    while i < len(command):
        token = command[i]
        if token in VALUE_FLAGS and i + 1 < len(command):
            lines.append(current)
            lines.append([token, command[i + 1]])
            current, i = [], i + 2
            continue
        if token.startswith(f"{LEAN_IMAGE_REPO}:"):
            lines.append(current)
            current = [token]
        else:
            current.append(token)
        i += 1
    lines.append(current)
    rendered = [" ".join(shlex.quote(t) for t in line) for line in lines if line]
    return [rendered[0]] + [indent + line for line in rendered[1:]]


@dataclass(frozen=True)
class Workspace:
    settings: dict  # its lean.json
    data: Path      # its data folder


def read_workspace(folder: Path) -> Workspace:
    """A lean-cli workspace (the folder `lean init` created): its lean.json and its data folder."""
    lean_json = folder / "lean.json"
    if not lean_json.is_file():
        raise LauncherError(f"{folder} is not a lean-cli workspace: it has no lean.json")
    settings = json.loads(strip_json_comments(lean_json.read_text(encoding="utf-8")))
    data = folder / settings.get("data-folder", "data")
    if not data.is_dir():
        raise LauncherError(f"The workspace data folder {data} (lean.json \"data-folder\") does not exist")
    return Workspace(settings, data.resolve())


def docker_command(lean_version: int, *, config: Path, plugins: Path, algorithm: Path, results: Path, storage: Path,
                   container_name: str, network: str | None, tty: bool, data: Path | None = None,
                   extra_env: list[str] = ()) -> list[str]:
    command = ["docker", "run", "--rm"]
    if tty:
        command.append("-it")
    command += ["--name", container_name]
    if network:
        command += ["--network", network]
    command += [
        "-v", f"{config}:/config/config.json:ro",
        "-v", f"{plugins}:/Lean/plugins:ro",
        "-v", f"{algorithm}:/Algorithm:ro",
        "-v", f"{results}:/Results",
        "-v", f"{storage}:/Storage",
    ]
    if data:
        # the workspace data replaces the image's /Lean/Data (the config's "data-folder"), as under lean-cli
        command += ["-v", f"{data}:/Lean/Data"]
    command += [
        "-e", "TZ_API_KEY_ID", "-e", "TZ_API_SECRET_KEY",
    ]
    for name in extra_env:  # names only: docker takes each value from the environment run.sh starts in
        command += ["-e", name]
    command += [
        f"{LEAN_IMAGE_REPO}:{lean_version}",
        "--config", "/config/config.json",
    ]
    return command


# ------------------------------------------------------------------------------------------- I/O helpers

def run(command: list[str], *, capture: bool = True, check: bool = True) -> str:
    result = subprocess.run(command, capture_output=capture, text=True)
    if check and result.returncode != 0:
        detail = (result.stderr or result.stdout or "").strip() if capture else ""
        raise LauncherError(f"command failed ({result.returncode}): {' '.join(command[:4])}...\n{detail}")
    return result.stdout if capture else ""


def http_json(url: str, token: str | None = None):
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": "application/json"})
    if token and url.startswith(GITHUB_API):
        request.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        raise LauncherError(f"GET {url}: HTTP {e.code}") from e


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def download(url: str, target: Path, token: str | None = None, api_asset: bool = False) -> None:
    """Downloads a file. GitHub API asset URLs answer with a redirect to signed storage, which must be
    followed without the GitHub token."""
    headers = {"User-Agent": USER_AGENT}
    if api_asset:
        headers["Accept"] = "application/octet-stream"
        if token:
            headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(url, headers=headers)
    opener = urllib.request.build_opener(_NoRedirect)
    try:
        response = opener.open(request, timeout=120)
    except urllib.error.HTTPError as e:
        if e.code in (301, 302, 303, 307, 308):
            response = urllib.request.urlopen(
                urllib.request.Request(e.headers["Location"], headers={"User-Agent": USER_AGENT}), timeout=120)
        else:
            raise LauncherError(f"download {url}: HTTP {e.code}") from e
    with response, open(target, "wb") as out:
        shutil.copyfileobj(response, out)


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def github_token() -> str | None:
    """GitHub token for private repositories: GITHUB_TOKEN, GH_TOKEN or the gh CLI's login. The ambient
    ones (GH_TOKEN, gh) are only used with the real api.github.com, never with a TZ_GITHUB_API_URL override."""
    if os.environ.get("GITHUB_TOKEN"):
        return os.environ["GITHUB_TOKEN"]
    if GITHUB_API != "https://api.github.com":
        return None
    if os.environ.get("GH_TOKEN"):
        return os.environ["GH_TOKEN"]
    if shutil.which("gh"):
        result = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True)
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip()
    return None


def resolve_repo(explicit: str | None) -> str:
    repo = explicit or os.environ.get("TZ_PLUGIN_REPO")
    if not repo:
        result = subprocess.run(["git", "-C", str(HERE), "remote", "get-url", "origin"], capture_output=True, text=True)
        match = re.search(r"github\.com[:/]([^/]+/[^/]+?)(?:\.git)?$", result.stdout.strip())
        repo = match.group(1) if match else None
    if not repo:
        raise LauncherError("Cannot tell which GitHub repository publishes the plugin: use --repo owner/name "
                            "(or set TZ_PLUGIN_REPO), or --plugin-zip for a local build.")
    return repo


def list_releases(repo: str, token: str | None) -> list[dict]:
    releases, page = [], 1
    while True:
        batch = http_json(f"{GITHUB_API}/repos/{repo}/releases?per_page=100&page={page}", token)
        if batch is None:
            raise LauncherError(f"Repository {repo} not found (private? set GITHUB_TOKEN or log in with gh).")
        releases += batch
        if len(batch) < 100:
            return releases
        page += 1


def local_lean_images() -> dict[int, str]:
    """Lean version -> local image id, from the lean_version label of local quantconnect/lean images."""
    out = run(["docker", "images", "--no-trunc", "--format", "{{.ID}}", LEAN_IMAGE_REPO])
    versions: dict[int, str] = {}
    for image_id in sorted(set(out.split())):
        label = run(["docker", "inspect", "--format", '{{ index .Config.Labels "lean_version" }}', image_id]).strip()
        if label.isdigit():
            versions[int(label)] = image_id
    return versions


def ensure_lean_image(version: int, local: dict[int, str]) -> None:
    tag = f"{LEAN_IMAGE_REPO}:{version}"
    if subprocess.run(["docker", "image", "inspect", tag], capture_output=True).returncode == 0:
        log(f"image {tag} is present")
    elif version in local:
        run(["docker", "tag", local[version], tag])
        log(f"tagged the local Lean {version} image as {tag}")
    else:
        log(f"pulling {tag} (about 27 GB the first time)...")
        run(["docker", "pull", tag], capture=False)


def unzip_plugin(zip_path: Path, target: Path) -> None:
    if target.exists():
        shutil.rmtree(target)
    target.mkdir(parents=True)
    with zipfile.ZipFile(zip_path) as archive:
        for member in archive.namelist():
            name = Path(member).name
            if name and not member.endswith("/"):
                (target / name).write_bytes(archive.read(member))
    if not (target / "QuantConnect.Brokerages.TradeZero.dll").exists():
        raise LauncherError(f"{zip_path} does not contain QuantConnect.Brokerages.TradeZero.dll")


def fetch_plugin_release(entry: dict, plugin_lean_version: int, cache: Path, token: str | None) -> Path:
    """Downloads (once) the asset built for plugin_lean_version; cached per release tag and Lean version."""
    target = cache / "tradezero" / f"{entry['tag']}-lean{plugin_lean_version}"
    if (target / "QuantConnect.Brokerages.TradeZero.dll").exists():
        log(f"TradeZero plugin {entry['tag']} built for Lean {plugin_lean_version}: cached")
        return target
    asset = entry["asset"]
    with tempfile.TemporaryDirectory() as tmp:
        zip_path = Path(tmp) / asset["name"]
        log(f"downloading {asset['name']} from release {entry['tag']}")
        if token:
            download(asset["url"], zip_path, token, api_asset=True)
        else:
            download(asset["browser_download_url"], zip_path)
        if entry.get("sha256"):
            sum_path = Path(tmp) / "asset.sha256"
            sha_asset = entry["sha256"]
            if token:
                download(sha_asset["url"], sum_path, token, api_asset=True)
            else:
                download(sha_asset["browser_download_url"], sum_path)
            expected = sum_path.read_text().split()[0].lower()
            if sha256_of(zip_path) != expected:
                raise LauncherError(f"checksum mismatch for {asset['name']}")
        else:
            log(f"warning: no {asset['name']}.sha256 in the release, checksum not verified")
        unzip_plugin(zip_path, target)
    return target


def fetch_feed(feed: Feed, version: int, cache: Path) -> Path | None:
    """Restores the feed package in the Lean image and keeps the files the image does not already have."""
    if feed.package is None:
        return None
    index = http_json(NUGET_INDEX.format(package=feed.package.lower()))
    if not index:
        raise LauncherError(f"{feed.package} not found on nuget.org")
    package_version = choose_feed_version(index["versions"], version)
    if not package_version:
        raise LauncherError(f"No {feed.package} version built for Lean {version} or older")
    target = cache / f"lean-{version}" / "feeds" / f"{feed.name}-{package_version}"
    if (target / ".complete").exists():
        log(f"{feed.package} {package_version}: cached")
        return target
    log(f"fetching {feed.package} {package_version} from nuget.org (inside {LEAN_IMAGE_REPO}:{version})")
    staging = target.with_name(target.name + ".tmp")
    if staging.exists():
        shutil.rmtree(staging)
    staging.mkdir(parents=True)
    # same rule as lean-cli: copy only what the Lean image does not already ship
    script = (
        "set -e; mkdir -p /tmp/h /tmp/feedfetch && cd /tmp/feedfetch"
        " && dotnet new classlib -f net10.0 -n feedfetch -o . >/dev/null"
        f" && dotnet add package {shlex.quote(feed.package)} --version {shlex.quote(package_version)} >/dev/null"
        " && dotnet publish -c Release -o /tmp/pub >/dev/null"
        f" && cd /tmp/pub && for f in *; do case $f in feedfetch.*) continue;; esac;"
        f" [ -e {LEAN_BIN}/$f ] || cp -r \"$f\" /out/; done"
    )
    run(["docker", "run", "--rm", "-u", f"{os.getuid()}:{os.getgid()}",
         "-e", "HOME=/tmp/h", "-e", "DOTNET_CLI_HOME=/tmp/h", "-e", "NUGET_PACKAGES=/tmp/h/nuget",
         "-e", "DOTNET_NOLOGO=1", "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
         "-v", f"{staging}:/out", "--entrypoint", "bash", f"{LEAN_IMAGE_REPO}:{version}", "-c", script])
    if not any(staging.glob("*.dll")):
        raise LauncherError(f"{feed.package} {package_version} produced no plugin file")
    (staging / ".complete").write_text(package_version)
    if target.exists():
        shutil.rmtree(target)
    staging.rename(target)
    return target


def assemble_plugins(cache: Path, version: int, feed: Feed, sources: list[Path]) -> Path:
    plugins = cache / f"lean-{version}" / f"plugins-{feed.name}"
    if plugins.exists():
        shutil.rmtree(plugins)
    plugins.mkdir(parents=True)
    for source in sources:
        for item in source.iterdir():
            if item.name.startswith("."):
                continue
            if item.is_dir():
                shutil.copytree(item, plugins / item.name)
            else:
                shutil.copy2(item, plugins / item.name)
    return plugins


def ask_feed_settings(feed: Feed, interactive: bool) -> dict:
    values = {}
    for setting in feed.settings:
        value = os.environ.get(setting.env)
        if not value and interactive:
            prompt = f"{feed.name} {setting.key}"
            if setting.choices:
                prompt += f" ({'/'.join(setting.choices)})"
            if setting.default:
                prompt += f" [{setting.default}]"
            prompt += ": "
            value = (getpass.getpass(prompt) if setting.secret else input(prompt)).strip()
        value = value or setting.default
        if not value:
            raise LauncherError(f"{setting.key} is required: set {setting.env} or run interactively")
        if setting.choices and value not in setting.choices:
            raise LauncherError(f"{setting.key} must be one of {', '.join(setting.choices)} (got {value!r})")
        values[setting.key] = value
    return values


def quantconnect_account(workspace: dict | None, interactive: bool, credentials_file: Path = QC_CREDENTIALS_FILE) -> dict:
    """job-user-id / api-access-token / job-organization-id from the environment, else from lean-cli's files
    (~/.lean/credentials after `lean login`, and the organization from a lean-cli workspace's lean.json),
    else asked for."""
    sources: dict = {}
    if credentials_file.exists():
        sources.update(json.loads(credentials_file.read_text(encoding="utf-8")))
    if workspace:
        organization = workspace.get("job-organization-id") or workspace.get("organization-id")
        if organization:
            sources["job-organization-id"] = organization
    values = {}
    for key, env, source_key in QC_SETTINGS:
        value = os.environ.get(env) or sources.get(source_key)
        if not value and interactive:
            ask = getpass.getpass if key == "api-access-token" else input
            value = ask(f"QuantConnect {key}: ").strip()
        if not value:
            raise LauncherError(f"{key} is required by QuantConnect's data feed modules: set {env}, run `lean login`"
                                f"{' and pass --workspace' if key == 'job-organization-id' else ''}, or run interactively")
        values[key] = str(value)
    return values


ENV_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


def parse_extra_env(items: list[str]) -> list[str]:
    """--extra-env NAME[,NAME...] (repeatable): the variable names, in order, without duplicates."""
    names: list[str] = []
    for item in items or []:
        for name in filter(None, (n.strip() for n in item.split(","))):
            if "=" in name:
                raise LauncherError(f"--extra-env takes variable names, not values ({name.split('=')[0]}=...): "
                                    "export the variable and pass its name")
            if not ENV_NAME.match(name):
                raise LauncherError(f"--extra-env: {name!r} is not an environment variable name")
            if name not in names and name not in ("TZ_API_KEY_ID", "TZ_API_SECRET_KEY"):
                names.append(name)
    return names


def parse_extra(items: list[str]) -> dict:
    extra = {}
    for item in items or []:
        key, sep, value = item.partition("=")
        if not sep:
            raise LauncherError(f"--set expects KEY=VALUE, got {item!r}")
        try:
            extra[key] = json.loads(value)
        except json.JSONDecodeError:
            extra[key] = value
    return extra


# ------------------------------------------------------------------------------------------- main

def parse_args(argv: list[str]) -> argparse.Namespace:
    feeds = ", ".join(f"{f.name} ({f.description})" for f in FEEDS.values())
    parser = argparse.ArgumentParser(
        description="Prepare LEAN live trading with the TradeZero plugin and a data feed, and print the docker command.",
        epilog=f"Data feeds: {feeds}. Feed keys come from POLYGON_API_KEY, DATABENTO_API_KEY, "
               "ALPACA_API_KEY/ALPACA_API_SECRET/ALPACA_ENVIRONMENT, or are asked for.")
    parser.add_argument("--data-feed", required=True, choices=sorted(FEEDS))
    parser.add_argument("--algorithm", required=True, type=Path,
                        help="Algorithm folder, mounted at /Algorithm (Python: contains main.py)")
    parser.add_argument("--algorithm-name", help="Algorithm class name (Python: detected from main.py)")
    parser.add_argument("--algorithm-file", help="File to run inside the folder (default main.py, or the DLL for C#)")
    parser.add_argument("--language", choices=("python", "csharp"), default="python")
    parser.add_argument("--environment", choices=("paper", "live"), default="paper",
                        help="TradeZero account type the brokerage may connect to (default paper)")
    parser.add_argument("--lean-version", type=int, help="Use this Lean version (must have a plugin release)")
    parser.add_argument("--plugin-version", help="Use the assets of this release tag (default: newest release)")
    parser.add_argument("--repo", help="GitHub repository publishing the plugin (owner/name)")
    parser.add_argument("--out", type=Path, default=Path("tradezero-run"), help="Output folder (default ./tradezero-run)")
    parser.add_argument("--cache", type=Path, default=Path.home() / ".cache" / "lean-tradezero")
    parser.add_argument("--workspace", type=Path,
                        help="lean-cli workspace: the QuantConnect organization id comes from its lean.json, and its "
                             "data folder (map and factor files, ...) is mounted at /Lean/Data")
    parser.add_argument("--extra-env", action="append", metavar="NAME[,NAME...]",
                        help="Pass these environment variables through to the Lean container (names only: the values "
                             "come from the environment run.sh starts in; repeatable)")
    parser.add_argument("--container-name", default="lean-tradezero")
    parser.add_argument("--set", action="append", metavar="KEY=VALUE",
                        help="Extra Lean config value (repeatable), e.g. --set tradezero-intraday-leverage=1")
    dev = parser.add_argument_group("testing / development")
    dev.add_argument("--plugin-zip", type=Path, help="Use a local tradezero-plugin-lean<V>.zip instead of a release")
    dev.add_argument("--tradezero-api-url", help="TradeZero API URL (e.g. a tz-mock)")
    dev.add_argument("--docker-network", help="Docker network for the Lean container")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    feed = FEEDS[args.data_feed]
    if feed.testing_only:
        log(f"warning: '{feed.name}' is {feed.description}")
    if shutil.which("docker") is None:
        raise LauncherError("docker is not on the PATH")

    algorithm = args.algorithm.resolve()
    if not algorithm.is_dir():
        raise LauncherError(f"{algorithm} is not a folder")
    if args.language == "python":
        algorithm_file = args.algorithm_file or "main.py"
        algorithm_name = args.algorithm_name or detect_algorithm_class(algorithm / algorithm_file)
    else:
        algorithm_file = args.algorithm_file
        algorithm_name = args.algorithm_name
        if not algorithm_file:
            raise LauncherError("--algorithm-file (the compiled DLL) is required for C#")
    if not (algorithm / algorithm_file).exists():
        raise LauncherError(f"{algorithm / algorithm_file} not found")
    if not algorithm_name:
        raise LauncherError("Cannot detect the algorithm class: use --algorithm-name")

    extra_env = parse_extra_env(args.extra_env)
    workspace = read_workspace(args.workspace.expanduser().resolve()) if args.workspace else None
    if workspace:
        log(f"data: {workspace.data}")

    cache = args.cache.expanduser().resolve()
    local = local_lean_images()

    # 1-2. Lean version (fixed tag) and plugin
    token = None
    if args.plugin_zip:
        zip_match = ASSET_PATTERN.match(args.plugin_zip.name)
        version = args.lean_version or (int(zip_match.group(1)) if zip_match else None)
        if version is None:
            raise LauncherError("Cannot tell the Lean version of --plugin-zip: name it tradezero-plugin-lean<V>.zip or pass --lean-version")
        log(f"Lean {version} (local plugin build {args.plugin_zip.name})")
        ensure_lean_image(version, local)
        plugin_dir = cache / f"lean-{version}" / "tradezero-local"
        unzip_plugin(args.plugin_zip.resolve(), plugin_dir)
    else:
        repo = resolve_repo(args.repo)
        token = github_token()
        assets = assets_by_lean_version(list_releases(repo, token), args.plugin_version)
        version, plugin_version, reason = choose_lean_version(list(local), list(assets), args.lean_version)
        log(reason)
        if plugin_version != version:
            log(f"TradeZero plugin: built for Lean {plugin_version}, the closest older release asset "
                f"(as for the data feeds); publish one for Lean {version} to match exactly")
        ensure_lean_image(version, local)
        plugin_dir = fetch_plugin_release(assets[plugin_version], plugin_version, cache, token)

    # 3. data feed
    feed_dir = fetch_feed(feed, version, cache)
    plugins = assemble_plugins(cache, version, feed, [plugin_dir] + ([feed_dir] if feed_dir else []))

    # 4. config and command
    template = json.loads(strip_json_comments(CONFIG_TEMPLATE.read_text(encoding="utf-8")))
    interactive = sys.stdin.isatty()
    feed_values = feed_config_values(feed, ask_feed_settings(feed, interactive))
    if feed.needs_quantconnect_account:
        feed_values.update(quantconnect_account(workspace.settings if workspace else None, interactive))
    location = f"/Algorithm/{algorithm_file}"
    config = build_config(template, feed, feed_values, algorithm_name=algorithm_name, language=args.language,
                          algorithm_location=location, environment=args.environment,
                          api_url=args.tradezero_api_url, extra=parse_extra(args.set))

    out = args.out.resolve()
    (out / "results").mkdir(parents=True, exist_ok=True)
    # the object store: the workspace's storage folder, as under lean-cli, else one next to run.sh
    storage = (args.workspace.expanduser().resolve() if workspace else out) / "storage"
    storage.mkdir(parents=True, exist_ok=True)
    log(f"object store: {storage}")
    config_path = out / "config.json"
    fd = os.open(config_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        json.dump(config, f, indent=2)
    os.chmod(config_path, 0o600)

    paths = dict(config=config_path, plugins=plugins, algorithm=algorithm, results=out / "results", storage=storage,
                 container_name=args.container_name, network=args.docker_network,
                 data=workspace.data if workspace else None, extra_env=extra_env)
    run_sh = out / "run.sh"
    # run.sh adds -it only when started from a terminal, so it also works in scripts and CI
    lines = format_command(docker_command(version, tty=False, **paths))
    lines[0] = lines[0].replace("docker run --rm", 'exec docker run --rm ${tty[@]+"${tty[@]}"}', 1)
    run_sh.write_text(
        "#!/usr/bin/env bash\n"
        "# Generated by tradezero_live.py. TradeZero keys come from TZ_API_KEY_ID / TZ_API_SECRET_KEY.\n"
        "set -euo pipefail\n"
        ': "${TZ_API_KEY_ID:?export TZ_API_KEY_ID first}" "${TZ_API_SECRET_KEY:?export TZ_API_SECRET_KEY first}"\n'
        "tty=()\n"
        "if [ -t 0 ]; then tty=(-it); fi\n"
        + " \\\n".join(lines) + "\n",
        encoding="utf-8")
    run_sh.chmod(0o700)

    log(f"plugins: {', '.join(sorted(p.name for p in plugins.iterdir()))}")
    log(f"config: {config_path} (mode 0600)")
    if not (os.environ.get("TZ_API_KEY_ID") and os.environ.get("TZ_API_SECRET_KEY")):
        log("export TZ_API_KEY_ID and TZ_API_SECRET_KEY before starting LEAN")
    print(" \\\n".join(format_command(docker_command(version, tty=True, **paths))))
    log(f"or run: {run_sh}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except LauncherError as error:
        log(f"error: {error}")
        sys.exit(1)
    except KeyboardInterrupt:
        sys.exit(130)
