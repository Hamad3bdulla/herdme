#!/usr/bin/env bash

set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "Usage: $0 <result-bundle.xcresult> [report.json]" >&2
  exit 64
fi

result_bundle=$1
report_path=${2:-macos-coverage.json}
target_name=${HERDME_COVERAGE_TARGET:-HerdMe.app}

[[ -d "$result_bundle" ]] || {
  echo "Coverage result bundle does not exist: $result_bundle" >&2
  exit 66
}

command -v ruby >/dev/null || {
  echo "Ruby is required to validate the coverage report." >&2
  exit 69
}
xcrun --find xccov >/dev/null

report_directory=$(dirname -- "$report_path")
mkdir -p "$report_directory"
temporary_report=$(mktemp "$report_directory/.herdme-coverage.XXXXXX")

cleanup() {
  rm -f -- "$temporary_report"
}
trap cleanup EXIT

xcrun xccov view --report --json "$result_bundle" > "$temporary_report"
mv -f -- "$temporary_report" "$report_path"

ruby -rjson - "$report_path" "$target_name" <<'RUBY'
report_path, target_name = ARGV
report = JSON.parse(File.binread(report_path))
target = report.fetch("targets").find { |entry| entry["name"] == target_name }
abort "Coverage target #{target_name.inspect} is missing." unless target

threshold = lambda do |name, fallback|
  value = Float(ENV.fetch(name, fallback))
  abort "#{name} must be between 0 and 100." unless value.finite? && value.between?(0, 100)
  value
rescue ArgumentError
  abort "#{name} must be a number between 0 and 100."
end

# Hosted runners intentionally skip live tests that require managed PHP/PHP-FPM/Xdebug.
target_floor = threshold.call("HERDME_MIN_APP_COVERAGE", "35.5")
domain_floor = threshold.call("HERDME_MIN_DOMAIN_COVERAGE", "67.5")
file_floor = threshold.call("HERDME_MIN_DOMAIN_FILE_COVERAGE", "10.0")

percent = ->(covered, executable) { executable.zero? ? 100.0 : covered.fdiv(executable) * 100 }
files = target.fetch("files")
domain_files = files.select do |file|
  path = file.fetch("path")
  path.include?("/HerdMe/Services/") || path.include?("/HerdMe/Models/")
end
abort "Coverage report contains no HerdMe service or model files." if domain_files.empty?

target_coverage = percent.call(target.fetch("coveredLines"), target.fetch("executableLines"))
domain_covered = domain_files.sum { |file| file.fetch("coveredLines") }
domain_executable = domain_files.sum { |file| file.fetch("executableLines") }
domain_coverage = percent.call(domain_covered, domain_executable)
undercovered = domain_files.filter_map do |file|
  executable = file.fetch("executableLines")
  next if executable.zero?
  coverage = percent.call(file.fetch("coveredLines"), executable)
  [file.fetch("name"), coverage] if coverage + 1e-9 < file_floor
end.sort_by { |name, coverage| [coverage, name] }

failures = []
if target_coverage + 1e-9 < target_floor
  failures << format("application coverage %.2f%% is below %.2f%%", target_coverage, target_floor)
end
if domain_coverage + 1e-9 < domain_floor
  failures << format("service/model coverage %.2f%% is below %.2f%%", domain_coverage, domain_floor)
end
unless undercovered.empty?
  details = undercovered.map { |name, coverage| format("%s %.2f%%", name, coverage) }.join(", ")
  failures << "service/model files below #{format('%.2f', file_floor)}%: #{details}"
end

puts format(
  "Coverage: application %.2f%% (%d/%d), services/models %.2f%% (%d/%d), minimum file %.2f%%.",
  target_coverage,
  target.fetch("coveredLines"),
  target.fetch("executableLines"),
  domain_coverage,
  domain_covered,
  domain_executable,
  domain_files.filter_map do |file|
    executable = file.fetch("executableLines")
    percent.call(file.fetch("coveredLines"), executable) unless executable.zero?
  end.min
)

abort "Coverage gate failed:\n- #{failures.join("\n- ")}" unless failures.empty?
RUBY
