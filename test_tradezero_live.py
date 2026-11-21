"""Unit tests for tradezero_live.py (no network, no docker): python3 -m unittest test_tradezero_live"""

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import tradezero_live as tz  # noqa: E402


def release(tag, *names, draft=False):
    return {"tag_name": tag, "draft": draft,
            "assets": [{"name": n, "url": f"api/{n}", "browser_download_url": f"dl/{tag}/{n}"} for n in names]}


class AssetsTests(unittest.TestCase):
    RELEASES = [  # GitHub lists the newest release first
        release("v1.1.0", "tradezero-plugin-lean18279.zip", "tradezero-plugin-lean18279.zip.sha256", "notes.txt"),
        release("v1.0.0", "tradezero-plugin-lean18274.zip", "tradezero-plugin-lean18279.zip"),
        release("v2.0.0-draft", "tradezero-plugin-lean18300.zip", draft=True),
    ]

    def test_newest_release_wins_and_drafts_are_ignored(self):
        assets = tz.assets_by_lean_version(self.RELEASES)
        self.assertEqual({18279, 18274}, set(assets))
        self.assertEqual("v1.1.0", assets[18279]["tag"])
        self.assertEqual("tradezero-plugin-lean18279.zip.sha256", assets[18279]["sha256"]["name"])
        self.assertEqual("v1.0.0", assets[18274]["tag"])
        self.assertIsNone(assets[18274]["sha256"])

    def test_plugin_version_restricts_to_one_release(self):
        assets = tz.assets_by_lean_version(self.RELEASES, "v1.0.0")
        self.assertEqual({18274, 18279}, set(assets))
        self.assertEqual("v1.0.0", assets[18279]["tag"])


class LeanVersionTests(unittest.TestCase):
    # choose_lean_version returns (Lean version to run, Lean version of the plugin asset, reason)

    def test_local_version_with_an_exact_release(self):
        self.assertEqual((18274, 18274), tz.choose_lean_version([18274], [18274, 18279])[:2])

    def test_local_version_uses_the_closest_older_release(self):
        # like the data feeds: no asset for 18278, the one built for 18274 is used on the local 18278 image
        self.assertEqual((18278, 18274), tz.choose_lean_version([18278], [18250, 18274, 18300])[:2])

    def test_local_version_older_than_every_release_falls_back_to_newest_supported(self):
        version, plugin, reason = tz.choose_lean_version([18250], [18274, 18279])
        self.assertEqual((18279, 18279), (version, plugin))
        self.assertIn("18250", reason)

    def test_no_local_image_uses_newest_supported(self):
        self.assertEqual((18279, 18279), tz.choose_lean_version([], [18274, 18279])[:2])

    def test_highest_local_image_is_considered(self):
        self.assertEqual((18279, 18279), tz.choose_lean_version([18274, 18279], [18274, 18279])[:2])

    def test_requested_version(self):
        self.assertEqual((18274, 18274), tz.choose_lean_version([18279], [18274, 18279], 18274)[:2])
        self.assertEqual((18277, 18274), tz.choose_lean_version([], [18274, 18279], 18277)[:2])
        with self.assertRaises(tz.LauncherError):
            tz.choose_lean_version([], [18279], 18200)

    def test_closest_not_newer(self):
        self.assertEqual(18274, tz.closest_not_newer([18250, 18274, 18300], 18278))
        self.assertIsNone(tz.closest_not_newer([18300], 18278))

    def test_no_release_at_all(self):
        with self.assertRaises(tz.LauncherError):
            tz.choose_lean_version([18274], [])


class FeedVersionTests(unittest.TestCase):
    VERSIONS = ["2.5.17000", "2.5.18247", "2.5.18257", "2.5.18300", "2.5.18257-beta", "10730.0.0"]

    def test_highest_version_not_newer_than_lean(self):
        self.assertEqual("2.5.18257", tz.choose_feed_version(self.VERSIONS, 18274))
        self.assertEqual("2.5.18257", tz.choose_feed_version(self.VERSIONS, 18257))
        self.assertEqual("2.5.18247", tz.choose_feed_version(self.VERSIONS, 18250))

    def test_none_when_all_are_newer(self):
        self.assertIsNone(tz.choose_feed_version(self.VERSIONS, 16000))


class ConfigTests(unittest.TestCase):
    def test_strip_comments_keeps_urls_in_strings(self):
        text = '{\n  // comment\n  "url": "https://webapi.tradezero.com", /* block */ "a": 1 // tail\n}'
        self.assertEqual({"url": "https://webapi.tradezero.com", "a": 1}, json.loads(tz.strip_json_comments(text)))

    def test_repository_template_parses(self):
        template = json.loads(tz.strip_json_comments(tz.CONFIG_TEMPLATE.read_text(encoding="utf-8")))
        self.assertEqual("TradeZeroBrokerage", template["environments"]["live-tradezero"]["live-mode-brokerage"])

    def test_build_config(self):
        template = {"environment": "x", "tradezero-api-key-id": "should-be-cleared", "data-queue-handler": ["X"]}
        feed = tz.FEEDS["polygon"]
        config = tz.build_config(template, feed, {"polygon-api-key": "k"}, algorithm_name="Algo", language="python",
                                 algorithm_location="/Algorithm/main.py", environment="paper", api_url=None,
                                 extra={"tradezero-intraday-leverage": 1})
        self.assertEqual("live-tradezero", config["environment"])
        self.assertEqual([feed.data_queue_handler], config["data-queue-handler"])
        self.assertEqual([feed.history_provider], config["history-provider"])
        self.assertEqual("/Lean/plugins", config["plugin-directory"])
        self.assertEqual("", config["tradezero-api-key-id"])
        self.assertEqual("k", config["polygon-api-key"])
        self.assertEqual(1, config["tradezero-intraday-leverage"])
        self.assertNotIn("tradezero-api-url", config)

    def test_fake_feed_forces_exchange_open(self):
        config = tz.build_config({}, tz.FEEDS["fake"], {}, algorithm_name="A", language="python",
                                 algorithm_location="/Algorithm/main.py", environment="paper",
                                 api_url="http://tzmock:8000", extra={})
        self.assertTrue(config["force-exchange-always-open"])
        self.assertEqual("http://tzmock:8000", config["tradezero-api-url"])

    def test_alpaca_environment_becomes_paper_trading_flag(self):
        feed = tz.FEEDS["alpaca"]
        self.assertTrue(tz.feed_config_values(feed, {"alpaca-environment": "paper"})["alpaca-paper-trading"])
        values = tz.feed_config_values(feed, {"alpaca-environment": "live", "alpaca-api-key": "k"})
        self.assertFalse(values["alpaca-paper-trading"])
        self.assertNotIn("alpaca-environment", values)

    def test_parse_extra(self):
        self.assertEqual({"a": 1, "b": "text", "c": True}, tz.parse_extra(["a=1", "b=text", "c=true"]))
        with self.assertRaises(tz.LauncherError):
            tz.parse_extra(["novalue"])


class QuantConnectAccountTests(unittest.TestCase):
    def setUp(self):
        self.saved = {k: os.environ.pop(k, None) for k in ("QC_USER_ID", "QC_API_ACCESS_TOKEN", "QC_ORGANIZATION_ID")}
        self.tmp = tempfile.TemporaryDirectory()
        self.credentials = Path(self.tmp.name) / "credentials"
        self.credentials.write_text(json.dumps({"user-id": "123", "api-token": "file-token"}))
        self.workspace = {"job-organization-id": "org-from-workspace"}

    def tearDown(self):
        for key, value in self.saved.items():
            if value is not None:
                os.environ[key] = value
            else:
                os.environ.pop(key, None)
        self.tmp.cleanup()

    def test_reads_lean_cli_files(self):
        values = tz.quantconnect_account(self.workspace, False, self.credentials)
        self.assertEqual({"job-user-id": "123", "api-access-token": "file-token",
                          "job-organization-id": "org-from-workspace"}, values)

    def test_environment_wins(self):
        os.environ["QC_API_ACCESS_TOKEN"] = "env-token"
        os.environ["QC_ORGANIZATION_ID"] = "env-org"
        values = tz.quantconnect_account(None, False, self.credentials)
        self.assertEqual("env-token", values["api-access-token"])
        self.assertEqual("env-org", values["job-organization-id"])

    def test_missing_organization_is_an_error_when_not_interactive(self):
        with self.assertRaises(tz.LauncherError):
            tz.quantconnect_account(None, False, self.credentials)

    def test_fake_feed_needs_no_account(self):
        self.assertFalse(tz.FEEDS["fake"].needs_quantconnect_account)
        self.assertTrue(all(f.needs_quantconnect_account for n, f in tz.FEEDS.items() if n != "fake"))


class MiscTests(unittest.TestCase):
    def test_detect_algorithm_class(self):
        with tempfile.TemporaryDirectory() as tmp:
            main = Path(tmp) / "main.py"
            main.write_text("from AlgorithmImports import *\n\nclass MyStrategy(QCAlgorithm):\n    pass\n")
            self.assertEqual("MyStrategy", tz.detect_algorithm_class(main))

    def test_docker_command_uses_a_fixed_tag_and_no_secrets(self):
        command = tz.docker_command(18274, config=Path("/o/config.json"), plugins=Path("/c/p"), algorithm=Path("/a"),
                                    results=Path("/o/results"), storage=Path("/w/storage"), container_name="lean-tradezero", network=None, tty=True)
        self.assertIn("quantconnect/lean:18274", command)
        self.assertNotIn("latest", " ".join(command))
        self.assertIn("TZ_API_KEY_ID", command)
        self.assertFalse(any("=" in c for c in command if c.startswith("TZ_")))

    def test_read_workspace(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            with self.assertRaisesRegex(tz.LauncherError, "no lean.json"):
                tz.read_workspace(folder)
            (folder / "lean.json").write_text('{\n  // lean-cli\n  "data-folder": "my-data",\n  "organization-id": "o"\n}')
            with self.assertRaisesRegex(tz.LauncherError, "does not exist"):
                tz.read_workspace(folder)
            (folder / "my-data").mkdir()
            workspace = tz.read_workspace(folder)
            self.assertEqual((folder / "my-data").resolve(), workspace.data)
            self.assertEqual("o", workspace.settings["organization-id"])
            (folder / "lean.json").write_text("{}")
            (folder / "data").mkdir()
            self.assertEqual((folder / "data").resolve(), tz.read_workspace(folder).data)

    def test_docker_command_mounts_the_object_store_read_write(self):
        command = tz.docker_command(18274, config=Path("/o/config.json"), plugins=Path("/c/p"), algorithm=Path("/a"),
                                    results=Path("/o/results"), storage=Path("/w/storage"),
                                    container_name="lean-tradezero", network=None, tty=False)
        self.assertIn("/w/storage:/Storage", command)  # no :ro
        self.assertEqual("/Storage", tz.build_config({}, tz.FEEDS["fake"], {}, algorithm_name="A", language="python",
                                                     algorithm_location="/Algorithm/main.py", environment="paper",
                                                     api_url=None, extra={})["object-store-root"])

    def test_docker_command_mounts_the_workspace_data(self):
        kwargs = dict(config=Path("/o/config.json"), plugins=Path("/c/p"), algorithm=Path("/a"),
                      results=Path("/o/results"), storage=Path("/w/storage"), container_name="lean-tradezero", network=None, tty=False)
        self.assertNotIn("/Lean/Data", " ".join(tz.docker_command(18274, **kwargs)))
        command = tz.docker_command(18274, data=Path("/w/data"), **kwargs)
        self.assertIn("/w/data:/Lean/Data", command)
        self.assertLess(command.index("/w/data:/Lean/Data"), command.index("quantconnect/lean:18274"))

    def test_parse_extra_env(self):
        self.assertEqual([], tz.parse_extra_env(None))
        self.assertEqual(["A", "B_2", "C"], tz.parse_extra_env(["A,B_2", " C ,A", "TZ_API_KEY_ID"]))
        with self.assertRaisesRegex(tz.LauncherError, "names, not values"):
            tz.parse_extra_env(["SECRET=value"])
        with self.assertRaisesRegex(tz.LauncherError, "not an environment variable name"):
            tz.parse_extra_env(["1BAD"])

    def test_docker_command_passes_extra_env_by_name(self):
        command = tz.docker_command(18274, config=Path("/o/config.json"), plugins=Path("/c/p"), algorithm=Path("/a"),
                                    results=Path("/o/results"), storage=Path("/w/storage"), container_name="lean-tradezero", network=None,
                                    tty=False, extra_env=["MASSIVE_API_KEY"])
        index = command.index("MASSIVE_API_KEY")
        self.assertEqual("-e", command[index - 1])
        self.assertLess(index, command.index("quantconnect/lean:18274"))

    def test_run_sh_mounts_a_new_timestamped_results_folder(self):
        import subprocess
        setup = tz.results_setup(Path("/o/my run/results"))
        self.assertIn("mkdir -p \"$results\"", setup)
        folder = subprocess.run(["bash", "-c", setup.split("\n")[0] + '; echo "$results"'],
                                capture_output=True, text=True, check=True).stdout.strip()
        self.assertRegex(folder, r"^/o/my run/results/\d{4}-\d{2}-\d{2}_\d{6}$")
        lines = tz.shell_lines(tz.docker_command(18274, config=Path("/o/config.json"), plugins=Path("/c/p"),
                                                 algorithm=Path("/a"), results=tz.RESULTS_VAR, storage=Path("/w/storage"),
                                                 container_name="lean-tradezero", network=None, tty=False))
        self.assertIn('  -v "${results}:/Results"', lines)

    def test_feeds_offered(self):
        self.assertEqual({"polygon", "databento", "alpaca", "fake"}, set(tz.FEEDS))


if __name__ == "__main__":
    unittest.main()
