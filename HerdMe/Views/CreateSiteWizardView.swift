import AppKit
import SwiftUI

struct CreateSiteWizardView: View {
    @EnvironmentObject private var model: AppModel
    @State private var step = 0
    @State private var template = SiteTemplate.laravel
    @State private var starterKit = StarterKit.none
    @State private var customStarterKit = ""
    @State private var projectName = ""
    @State private var projectParent: URL = ProjectCreator.defaultProjectsDirectory
    @State private var testingFramework = "Pest"
    @State private var installBoost = true
    @State private var initializeGit = false
    @State private var existingURL: URL?
    @State private var isCreating = false
    @State private var isCancelling = false
    @State private var creationTask: Task<Void, Never>?
    @State private var creationStage: ProjectCreationStage?
    @State private var creationError: ProjectCreationFailure?
    @State private var isShowingCreationDetails = false
    @State private var createdSiteURL: URL?
    @FocusState private var focusedField: FocusedField?

    private enum FocusedField {
        case customStarterKit
        case projectName
    }

    private enum SiteTemplate: String, CaseIterable, Identifiable {
        case laravel = "New Laravel Project"
        case existing = "Link existing project"
        var id: String { rawValue }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("Create a New Site").font(.system(size: 18, weight: .semibold))
                Spacer()
            }
            .padding(.horizontal, 16)
            .frame(height: 58)
            Divider()

            Group {
                switch step {
                case 0: templateStep
                case 1: starterStep
                default: configurationStep
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            Divider()
            HStack {
                if isShowingCreationProgress {
                    creationFooter
                } else {
                    Button("Cancel") { closeWindow() }
                        .keyboardShortcut(.cancelAction)
                    Spacer()
                    if step > 0 && !(step == 1 && template == .existing) {
                        Button("Previous") { step -= 1 }
                    }
                    if step == 0 || (step == 1 && template == .laravel) {
                        Button("Next") { step += 1 }
                            .buttonStyle(.borderedProminent)
                            .keyboardShortcut(.defaultAction)
                            .disabled(
                                template == .existing && existingURL == nil
                                    || step == 1 && starterKit == .custom
                                        && customStarterKit.trimmingCharacters(
                                            in: .whitespacesAndNewlines
                                        ).isEmpty
                            )
                    } else if template == .existing {
                        Button("Link Project") { linkExisting() }
                            .buttonStyle(.borderedProminent)
                            .keyboardShortcut(.defaultAction)
                            .disabled(existingURL == nil)
                    } else {
                        Button("Create") { createProject() }
                            .buttonStyle(.borderedProminent)
                            .keyboardShortcut(.defaultAction)
                            .disabled(projectName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    }
                }
            }
            .padding(16)
        }
        .onAppear {
            try? FileManager.default.createDirectory(
                at: projectParent,
                withIntermediateDirectories: true
            )
        }
        .onDisappear { creationTask?.cancel() }
        .onChange(of: step) { _ in updateFocusedField() }
        .onChange(of: starterKit) { _ in updateFocusedField() }
    }

    private var templateStep: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("Choose a Template for Your New Site").font(.title2.weight(.semibold))
            HStack(spacing: 60) {
                templateCard(.laravel, symbol: "l.square.fill", tint: .red)
                templateCard(.existing, symbol: "folder", tint: .blue)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .padding(24)
    }

    private var starterStep: some View {
        VStack(alignment: .leading, spacing: 18) {
            if template == .laravel {
                Text("Which Starter Kit Would You Like To Install?").font(.title2.weight(.semibold))
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 130))], spacing: 24) {
                    ForEach(StarterKit.allCases) { kit in
                        Button { starterKit = kit } label: {
                            VStack(spacing: 8) {
                                Image(systemName: kit.symbol).font(.system(size: 42)).foregroundStyle(kit == starterKit ? Color.accentColor : Color.secondary)
                                Text(kit.rawValue)
                                    .font(.callout)
                                    .foregroundStyle(kit == starterKit ? Color.accentColor : Color.primary)
                            }
                            .frame(maxWidth: .infinity, minHeight: 90)
                            .padding(8)
                            .background(kit == starterKit ? Color.accentColor.opacity(0.12) : Color.clear)
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                        }
                        .buttonStyle(.plain)
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                if starterKit == .custom {
                    HStack(spacing: 12) {
                        Text("Composer Package:")
                            .frame(width: 150, alignment: .trailing)
                        TextField("vendor/package", text: $customStarterKit)
                            .textFieldStyle(.roundedBorder)
                            .focused($focusedField, equals: .customStarterKit)
                    }
                    .frame(maxWidth: 520)
                    .frame(maxWidth: .infinity)
                }
            } else {
                Text("Choose an Existing Project").font(.title2.weight(.semibold))
                VStack(spacing: 12) {
                    Text(existingURL?.path ?? "No project selected")
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                    Button("Choose Project Folder") { chooseExistingProject() }
                        .buttonStyle(.borderedProminent)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .padding(24)
    }

    private var configurationStep: some View {
        Group {
            if isShowingCreationProgress {
                creationProgressStep
            } else {
                configurationForm
            }
        }
    }

    private var configurationForm: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("Configure Your Site").font(.title2.weight(.semibold))
            VStack(spacing: 14) {
                labeledField("Project Name") {
                    TextField("What is the name of your project?", text: $projectName)
                        .textFieldStyle(.roundedBorder)
                        .focused($focusedField, equals: .projectName)
                }
                labeledField("Project Path") {
                    HStack {
                        Text(projectParent.path.replacingOccurrences(of: FileManager.default.homeDirectoryForCurrentUser.path, with: "~"))
                        Spacer()
                        Button { chooseProjectParent() } label: { Image(systemName: "chevron.up.chevron.down") }
                            .buttonStyle(.borderless)
                    }
                    .padding(7)
                    .background(Color(nsColor: .controlBackgroundColor))
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                }
                labeledField("Testing Framework") {
                    Picker("", selection: $testingFramework) {
                        Text("Pest").tag("Pest")
                        Text("PHPUnit").tag("PHPUnit")
                    }
                    .labelsHidden()
                }
                Toggle("Install Laravel Boost", isOn: $installBoost).toggleStyle(.switch)
                Toggle("Initialize a Git repository", isOn: $initializeGit).toggleStyle(.switch)
            }
            .frame(maxWidth: 570)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .padding(24)
    }

    private var creationProgressStep: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                VStack(alignment: .leading, spacing: 5) {
                    Text(creationHeading)
                        .font(.title2.weight(.semibold))
                    Text(projectDestination.path)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .textSelection(.enabled)
                }

                VStack(alignment: .leading, spacing: 13) {
                    ForEach(creationStages) { stage in
                        creationStageRow(stage)
                    }
                }

                if let creationError {
                    VStack(alignment: .leading, spacing: 6) {
                        Label("Creation could not be completed", systemImage: "exclamationmark.triangle.fill")
                            .font(.headline)
                            .foregroundStyle(.red)
                        Text(creationError.message)
                            .font(.callout)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .textSelection(.enabled)
                        if let details = creationError.technicalDetails {
                            DisclosureGroup(
                                "Technical Details",
                                isExpanded: $isShowingCreationDetails
                            ) {
                                ScrollView {
                                    Text(details)
                                        .font(.system(.caption, design: .monospaced))
                                        .frame(maxWidth: .infinity, alignment: .leading)
                                        .textSelection(.enabled)
                                }
                                .frame(maxHeight: 130)
                                .padding(.top, 4)
                            }
                        }
                    }
                    .padding(12)
                    .background(Color.red.opacity(0.09))
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                }
            }
            .frame(maxWidth: 650, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            .padding(24)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    @ViewBuilder
    private func creationStageRow(_ stage: ProjectCreationStage) -> some View {
        let state = creationState(for: stage)
        HStack(alignment: .top, spacing: 12) {
            Group {
                switch state {
                case .pending:
                    Image(systemName: "circle")
                        .foregroundStyle(.tertiary)
                case .active:
                    ProgressView()
                        .controlSize(.small)
                case .completed:
                    Image(systemName: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                case .failed:
                    Image(systemName: "xmark.circle.fill")
                        .foregroundStyle(.red)
                }
            }
            .frame(width: 18, height: 18)

            VStack(alignment: .leading, spacing: 2) {
                Text(stage.title)
                    .font(.body.weight(state == .active ? .semibold : .regular))
                Text(stage.detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .opacity(state == .pending ? 0.55 : 1)
        .accessibilityElement(children: .combine)
    }

    @ViewBuilder
    private var creationFooter: some View {
        Button(isCreating ? (isCancelling ? "Cancelling..." : "Cancel") : (createdSiteURL == nil ? "Cancel" : "Close")) {
            if isCreating {
                cancelCreation()
            } else {
                closeWindow()
            }
        }
        .disabled(isCancelling)
        Spacer()

        if isCreating {
            ProgressView()
                .controlSize(.small)
            Text(creationStage?.title ?? "Starting...")
                .foregroundStyle(.secondary)
        } else if createdSiteURL != nil {
            Button("Done") { closeWindow() }
                .buttonStyle(.borderedProminent)
        } else if creationError != nil {
            Button("Edit Configuration") { resetCreationState() }
            if canRetryCreation {
                Button("Try Again") { createProject() }
                    .buttonStyle(.borderedProminent)
            }
        }
    }

    private func templateCard(_ value: SiteTemplate, symbol: String, tint: Color) -> some View {
        Button { template = value } label: {
            VStack(spacing: 12) {
                Image(systemName: symbol).font(.system(size: 54)).foregroundStyle(tint)
                Text(value.rawValue).font(.title3)
            }
            .frame(width: 180, height: 150)
            .background(template == value ? Color.accentColor.opacity(0.16) : Color.clear)
            .clipShape(RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(.plain)
    }

    private func labeledField<Content: View>(_ label: String, @ViewBuilder content: () -> Content) -> some View {
        HStack {
            Text("\(label):").frame(width: 150, alignment: .trailing)
            content()
        }
    }

    private func chooseProjectParent() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.begin { response in
            guard response == .OK, let url = panel.url else { return }
            projectParent = url
        }
    }

    private func chooseExistingProject() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.begin { response in
            guard response == .OK else { return }
            existingURL = panel.url
        }
    }

    private func updateFocusedField() {
        if step == 1, template == .laravel, starterKit == .custom {
            focusedField = .customStarterKit
        } else if step >= 2, template == .laravel {
            focusedField = .projectName
        } else {
            focusedField = nil
        }
    }

    private func linkExisting() {
        guard let existingURL else { return }
        do {
            try model.linkExistingSite(at: existingURL)
            closeWindow()
        } catch {
            model.lastError = error.localizedDescription
        }
    }

    private func createProject() {
        isCreating = true
        isCancelling = false
        creationError = nil
        isShowingCreationDetails = false
        createdSiteURL = nil
        creationStage = .validatingRequest
        let request = NewProjectRequest(
            name: projectName,
            parentDirectory: projectParent,
            starterKit: starterKit,
            customStarterKit: starterKit == .custom ? customStarterKit : nil,
            testingFramework: testingFramework,
            installBoost: installBoost,
            initializeGit: initializeGit
        )
        creationTask?.cancel()
        creationTask = Task { @MainActor in
            do {
                let url = try await model.createProject(request) { stage in
                    creationStage = stage
                }
                creationStage = .registeringSite
                model.registerCreatedSite(at: url)
                createdSiteURL = url
                creationStage = .completed
            } catch {
                creationError = ProjectCreationFailure(error)
            }
            isCreating = false
            isCancelling = false
            creationTask = nil
        }
    }

    private var creationStages: [ProjectCreationStage] {
        ProjectCreationStage.stages(
            installBoost: installBoost,
            buildFrontendAssets: starterKit.requiresFrontendAssets,
            initializeGit: initializeGit
        )
    }

    private var projectDestination: URL {
        projectParent.appendingPathComponent(
            projectName.trimmingCharacters(in: .whitespacesAndNewlines),
            isDirectory: true
        )
    }

    private var isShowingCreationProgress: Bool {
        creationStage != nil || creationError != nil || createdSiteURL != nil
    }

    private var creationHeading: String {
        if createdSiteURL != nil { return "Your Site Is Ready" }
        if creationError != nil { return "Site Creation Stopped" }
        return "Creating \(projectName.trimmingCharacters(in: .whitespacesAndNewlines))"
    }

    private var canRetryCreation: Bool {
        !FileManager.default.fileExists(atPath: projectDestination.path)
    }

    private enum CreationStageState {
        case pending
        case active
        case completed
        case failed
    }

    private func creationState(for stage: ProjectCreationStage) -> CreationStageState {
        guard let creationStage,
              let stageIndex = creationStages.firstIndex(of: stage),
              let currentIndex = creationStages.firstIndex(of: creationStage) else {
            return .pending
        }
        if createdSiteURL != nil || stageIndex < currentIndex { return .completed }
        if stageIndex > currentIndex { return .pending }
        return creationError == nil ? .active : .failed
    }

    private func resetCreationState() {
        isCreating = false
        isCancelling = false
        creationTask?.cancel()
        creationTask = nil
        creationStage = nil
        creationError = nil
        isShowingCreationDetails = false
        createdSiteURL = nil
    }

    private func closeWindow() {
        NSApplication.shared.keyWindow?.close()
    }

    private func cancelCreation() {
        guard isCreating, !isCancelling else { return }
        isCancelling = true
        creationTask?.cancel()
    }
}
