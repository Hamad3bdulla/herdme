import SwiftUI

struct SidebarView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Spacer().frame(height: 50)

            ForEach(SidebarPage.visibleCases) { page in
                Button {
                    if page == .logs {
                        model.showApplicationLogs()
                    } else {
                        model.selectedPage = page
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

                        Text(page.rawValue)
                            .font(.system(size: 14, weight: model.selectedPage == page ? .medium : .regular))
                            .lineLimit(1)
                        Spacer()
                    }
                    .foregroundStyle(model.selectedPage == page ? Color.white : Color.secondary)
                    .padding(.horizontal, 8)
                    .frame(height: 34)
                    .background(model.selectedPage == page ? Color.accentColor : Color.clear)
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(page.rawValue)
                .accessibilityAddTraits(model.selectedPage == page ? .isSelected : [])
                .padding(.horizontal, 10)
            }

            Spacer()
        }
        .frame(width: 180)
        .background(Color(nsColor: .windowBackgroundColor))
    }
}
