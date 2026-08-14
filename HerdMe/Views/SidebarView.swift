import SwiftUI

struct SidebarView: View {
    @EnvironmentObject private var navigation: AppNavigation

    private let overviewPages: [SidebarPage] = [.dashboard]
    private let environmentPages: [SidebarPage] = [.general, .sites, .php, .node, .services]
    private let toolPages: [SidebarPage] = [.mail, .dumps, .debugger, .logs]

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Spacer().frame(height: 50)

            pageGroup(overviewPages)
            sidebarDivider
            pageGroup(environmentPages)
            sidebarDivider
            pageGroup(toolPages)

            Spacer()
            sidebarDivider
            sidebarButton(.about)
            Spacer().frame(height: 12)
        }
        .frame(width: 180)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private func pageGroup(_ pages: [SidebarPage]) -> some View {
        ForEach(pages) { page in
            sidebarButton(page)
        }
    }

    private var sidebarDivider: some View {
        Divider()
            .padding(.horizontal, 18)
            .padding(.vertical, 5)
    }

    private func sidebarButton(_ page: SidebarPage) -> some View {
        Button {
            if page == .logs {
                navigation.showApplicationLogs()
            } else {
                navigation.selectedPage = page
            }
        } label: {
            HStack(spacing: 9) {
                ZStack {
                    RoundedRectangle(cornerRadius: 5)
                        .fill(page.tint.gradient)
                    Image(systemName: page.symbol)
                        .font(.system(size: 14, weight: .medium))
                        .foregroundStyle(.white)
                }
                .frame(width: 22, height: 22)

                Text(page.localizedTitle)
                    .font(.system(size: 14, weight: navigation.selectedPage == page ? .medium : .regular))
                    .lineLimit(1)
                Spacer()
            }
            .foregroundStyle(navigation.selectedPage == page ? Color.white : Color.secondary)
            .padding(.horizontal, 8)
            .frame(height: 34)
            .background(navigation.selectedPage == page ? Color.accentColor : Color.clear)
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(page.localizedTitle)
        .accessibilityIdentifier("sidebar.\(page.rawValue.lowercased())")
        .accessibilityAddTraits(navigation.selectedPage == page ? .isSelected : [])
        .padding(.horizontal, 10)
    }
}
