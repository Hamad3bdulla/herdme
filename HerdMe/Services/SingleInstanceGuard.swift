import Darwin
import Foundation

final class SingleInstanceGuard {
    let acquired: Bool

    private var descriptor: Int32 = -1

    init(lockURL: URL, fileManager: FileManager = .default) {
        do {
            try fileManager.createDirectory(
                at: lockURL.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
        } catch {
            acquired = false
            return
        }

        let openedDescriptor = open(lockURL.path, O_CREAT | O_RDWR, S_IRUSR | S_IWUSR)
        guard openedDescriptor >= 0 else {
            acquired = false
            return
        }
        guard flock(openedDescriptor, LOCK_EX | LOCK_NB) == 0 else {
            close(openedDescriptor)
            acquired = false
            return
        }

        descriptor = openedDescriptor
        acquired = true
    }

    deinit {
        guard descriptor >= 0 else { return }
        flock(descriptor, LOCK_UN)
        close(descriptor)
    }
}
