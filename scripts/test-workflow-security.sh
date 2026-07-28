#!/usr/bin/env bash

set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
workflow_directory="$repository_root/.github/workflows"

command -v ruby >/dev/null || { echo "Ruby is required to validate workflows." >&2; exit 69; }

ruby -ryaml - "$workflow_directory" <<'RUBY'
workflow_directory = ARGV.fetch(0)
workflow_paths = Dir.glob(File.join(workflow_directory, "*.yml")).sort
abort "No GitHub workflows were found." if workflow_paths.empty?

workflow_paths.each do |path|
  source = File.binread(path)
  abort "#{path} uses pull_request_target, which can expose privileges to untrusted code." if \
    source.match?(/^\s*pull_request_target\s*:/)
  document = YAML.safe_load(source, permitted_classes: [], aliases: false)
  abort "#{path} is not a workflow mapping." unless document.is_a?(Hash)
  permissions = document["permissions"]
  abort "#{path} must declare top-level least-privilege permissions." unless permissions.is_a?(Hash)
  expected_permissions = if File.basename(path) == "codeql.yml"
    { "contents" => "read", "security-events" => "write" }
  else
    { "contents" => "read" }
  end
  abort "#{path} top-level permissions exceed the approved contract." unless \
    permissions == expected_permissions

  jobs = document["jobs"]
  abort "#{path} has no jobs." unless jobs.is_a?(Hash) && !jobs.empty?
  jobs.each do |job_name, job|
    next unless job.is_a?(Hash)
    job_permissions = job["permissions"]
    if job_permissions
      expected_job_permissions = if File.basename(path) == "release.yml" && job_name == "publish"
        { "contents" => "write", "id-token" => "write", "attestations" => "write" }
      end
      abort "#{path} job #{job_name} requests unapproved elevated permissions." unless \
        job_permissions == expected_job_permissions
    end
    steps = job["steps"]
    next unless steps.is_a?(Array)
    steps.each do |step|
      next unless step.is_a?(Hash) && step["uses"].is_a?(String)
      action = step["uses"]
      abort "#{path} job #{job_name} uses an action without an immutable SHA: #{action}" unless \
        action.match?(/\A[^@\s]+@[0-9a-f]{40}\z/)
      next unless action.start_with?("actions/checkout@")
      options = step["with"]
      abort "#{path} job #{job_name} checkout must set persist-credentials: false." unless \
        options.is_a?(Hash) && options["persist-credentials"] == false
    end
  end
end

release_path = File.join(workflow_directory, "release.yml")
release_source = File.binread(release_path)
identity_index = release_source.index("- name: Validate signed release identity")
secret_index = release_source.index("secrets.")
abort "The release workflow must validate the signed tag before referencing a secret." unless \
  identity_index && secret_index && identity_index < secret_index
abort "The release workflow must run the signed-tag verifier." unless \
  release_source.include?("./scripts/verify-release-tag.sh")

puts "Validated #{workflow_paths.length} least-privilege GitHub workflows."
RUBY
