import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";

import Settings from "../settings";
import { DummyDataProvider } from "@/context/dummy-data-context";

// #309 (S6): Settings is now a controlled component -- machineStatuses/happyHourMode come in as
// props and Settings itself keeps no local mirror of them, so a test harness that wants to see
// the UI reflect a change (the same way App.tsx really drives it) must hold that state itself and
// feed it back in, exactly like a real parent would. A onMachineStatusChange/onHappyHourModeChange
// override is still called first so callers can assert on it directly.
function ControlledSettings(props: Partial<React.ComponentProps<typeof Settings>>) {
    const { machines, happyHour, onMachineStatusChange, onHappyHourModeChange, ...rest } = props;
    const [machineStatuses, setMachineStatuses] = useState<Record<string, "up" | "down">>(
        () => Object.fromEntries(Object.entries(machines ?? {}).map(([machine, detail]) => [machine, detail.status]))
    );
    const [happyHourMode, setHappyHourMode] = useState<"auto" | "on" | "off">("auto");

    return (
        <Settings
            isMobile={false}
            showSessionTokens={false}
            onShowSessionTokensChange={() => {}}
            verboseLogging={false}
            onVerboseLoggingChange={() => {}}
            logToFile={false}
            onLogToFileChange={() => {}}
            voiceChoice="marin"
            onVoiceChoiceChange={() => {}}
            machines={machines}
            happyHour={happyHour}
            machineStatuses={machineStatuses}
            onMachineStatusChange={(machine, status) => {
                setMachineStatuses(current => ({ ...current, [machine]: status }));
                onMachineStatusChange?.(machine, status);
            }}
            happyHourMode={happyHourMode}
            onHappyHourModeChange={mode => {
                setHappyHourMode(mode);
                onHappyHourModeChange?.(mode);
            }}
            {...rest}
        />
    );
}

function renderSettings(props: Partial<React.ComponentProps<typeof Settings>> = {}) {
    return render(
        <DummyDataProvider>
            <ControlledSettings {...props} />
        </DummyDataProvider>
    );
}

describe("Settings store operations", () => {
    it("renders one machine toggle per declared machine with the pack default state and a human label", async () => {
        renderSettings({
            machines: {
                ice_cream_machine: { status: "down", label: "Ice cream machine is down for cleaning" },
                slush_machine: { status: "up", label: "Slush machine is down" }
            }
        });
        fireEvent.click(screen.getByRole("button", { name: /open settings/i }));

        expect(screen.getByText("Store operations")).toBeInTheDocument();
        expect(screen.getByText("Ice cream machine")).toBeInTheDocument();
        expect(screen.getByText("Slush machine")).toBeInTheDocument();
        expect(screen.getByLabelText("Toggle Ice cream machine status")).not.toBeChecked();
        expect(screen.getByLabelText("Toggle Slush machine status")).toBeChecked();
    });

    it("optimistically flips a machine toggle and calls back with the machine key and next status", async () => {
        const onMachineStatusChange = vi.fn();
        renderSettings({
            machines: {
                ice_cream_machine: { status: "down", label: "Ice cream machine is down for cleaning" }
            },
            onMachineStatusChange
        });
        fireEvent.click(screen.getByRole("button", { name: /open settings/i }));

        const toggle = screen.getByLabelText("Toggle Ice cream machine status");
        expect(toggle).not.toBeChecked();

        await userEvent.click(toggle);

        expect(onMachineStatusChange).toHaveBeenCalledWith("ice_cream_machine", "up");
        expect(toggle).toBeChecked();
        expect(screen.getByText("Up")).toBeInTheDocument();
    });

    it("renders no happy-hour control when the persona has no happy hour", async () => {
        renderSettings({ happyHour: null });
        fireEvent.click(screen.getByRole("button", { name: /open settings/i }));
        expect(screen.queryByRole("radiogroup", { name: /happy hour mode/i })).not.toBeInTheDocument();
    });

    it("renders the happy-hour control when the persona declares happy hour", async () => {
        renderSettings({ happyHour: { startHour: 14, endHour: 17 } });
        fireEvent.click(screen.getByRole("button", { name: /open settings/i }));
        expect(await screen.findByRole("radiogroup", { name: /happy hour mode/i })).toBeInTheDocument();
    });

    it("selects auto, on, and off for happy hour and reflects the local selection", async () => {
        const onHappyHourModeChange = vi.fn();
        renderSettings({
            happyHour: { startHour: 14, endHour: 17 },
            onHappyHourModeChange
        });
        fireEvent.click(screen.getByRole("button", { name: /open settings/i }));

        const auto = screen.getByRole("radio", { name: "Auto" });
        const on = screen.getByRole("radio", { name: "On" });
        const off = screen.getByRole("radio", { name: "Off" });

        expect(auto).toHaveAttribute("aria-checked", "true");

        fireEvent.click(on);
        expect(onHappyHourModeChange).toHaveBeenCalledWith("on");
        expect(screen.getByRole("radio", { name: "On" })).toHaveAttribute("aria-checked", "true");

        fireEvent.click(off);
        expect(onHappyHourModeChange).toHaveBeenCalledWith("off");
        expect(screen.getByRole("radio", { name: "Off" })).toHaveAttribute("aria-checked", "true");

        fireEvent.click(screen.getByRole("radio", { name: "Auto" }));
        expect(onHappyHourModeChange).toHaveBeenCalledWith("auto");
        expect(screen.getByRole("radio", { name: "Auto" })).toHaveAttribute("aria-checked", "true");
    });

    it("does not render the store-operations section when there are no machines and no happy hour", async () => {
        renderSettings({ machines: {}, happyHour: null });
        fireEvent.click(screen.getByRole("button", { name: /open settings/i }));

        expect(screen.queryByText("Store operations")).not.toBeInTheDocument();
        expect(screen.queryByRole("radiogroup", { name: /happy hour mode/i })).not.toBeInTheDocument();
    });
});
