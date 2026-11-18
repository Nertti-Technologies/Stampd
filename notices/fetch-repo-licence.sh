#!/usr/bin/env bash
# Fetches a GitHub repository's licence (and NOTICE, when present) into notices/inputs/repos/
# for packages that declare a licence but ship no licence text. Each file records the exact
# blob URL (pinned to the commit) and the retrieval date, as the notices generator requires.
#
#   notices/fetch-repo-licence.sh https://github.com/serilog/serilog [...more repo URLs]
#
# Needs an authenticated `gh` CLI. Review the fetched text before committing it.
set -euo pipefail

out_dir="$(cd "$(dirname "$0")" && pwd)/inputs/repos"
mkdir -p "$out_dir"
today="$(date -u +%Y-%m-%d)"

for url in "$@"; do
  slug="$(echo "$url" | sed -E 's#^https?://github.com/##; s#\.git$##; s#/+$##')"
  owner="${slug%%/*}"; repo="${slug#*/}"
  sha="$(gh api "repos/$slug/commits/HEAD" --jq .sha)"
  file="$out_dir/${owner}__${repo}.txt"

  path="$(gh api "repos/$slug/license" --jq .path)"
  {
    echo "Source: https://github.com/$slug/blob/$sha/$path"
    echo "Retrieved: $today"
    echo
    gh api "repos/$slug/contents/$path?ref=$sha" --jq .content | base64 --decode
  } > "$file"

  notice="$(gh api "repos/$slug/contents?ref=$sha" --jq '[.[] | select(.type=="file") | .name | select(test("^NOTICE"; "i"))][0] // empty')"
  if [[ -n "$notice" ]]; then
    {
      echo "Source: https://github.com/$slug/blob/$sha/$notice"
      echo "Retrieved: $today"
      echo
      gh api "repos/$slug/contents/$notice?ref=$sha" --jq .content | base64 --decode
    } > "$out_dir/${owner}__${repo}.NOTICE.txt"
  fi
  echo "fetched $slug ($path${notice:+, $notice}) at ${sha:0:12}"
done
